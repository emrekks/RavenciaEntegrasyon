using System.Linq.Expressions;
using System.Text.RegularExpressions;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;
using static MarketplaceHub.Infrastructure.Persistence.InvoiceLabelPolicy;
using static MarketplaceHub.Infrastructure.Persistence.OrderSnapshotReader;

namespace MarketplaceHub.Infrastructure.Persistence;

// SIPARIS-STOK-9: the Returns page's invoice label ("fatura etiketi") read by the database. The rule is InvoiceLabel over
// the claim's order, the order's latest original invoice from an operational provider connection, and its latest package
// (MarketplaceSalesService.ReturnInvoiceLabelsAsync before 16 Nov 2026), written as one expression per claim: a
// correlated sub-query per input (each an index read of the claim's own order), so the cost follows the claims read, not
// the tenant's orders. Everything but the customer snapshot is decided there. The snapshot is read (JsonText, a
// depth-first search of every property in document order) only when nothing else decides; the database then gives
// BILINMIYOR unless the snapshot's raw text may hold one of the invoice words (ReturnSearchText.JsonWordsPattern, a
// superset), and marks the few others (SnapshotLabel) for JsonText itself, whose labels go back in as id lists.
public sealed partial class MarketplaceSalesService
{
    internal sealed class ReturnLabelRow
    {
        public Guid Id { get; init; }
        public string Label { get; init; } = "";
    }

    // The label the database cannot decide: the customer snapshot decides it in memory.
    internal const string SnapshotLabel = "@SNAPSHOT";

    private static readonly string InvoicedStatusPattern = ReturnSearchText.TrimmedIgnoreCasePattern("INVOICED");
    private static readonly string BlankTextPattern = ReturnSearchText.BlankPattern;
    // INVOICED, RECEIVED, REJECTED, NOTINVOICED and NOT_INVOICED all hold one of these.
    private static readonly string SnapshotInvoiceWordsPattern = ReturnSearchText.JsonWordsPattern("INVOICED", "RECEIVED", "REJECTED");

    // The claims whose label JsonText decided, by label (an empty set before the snapshots were read).
    internal sealed record ReturnSnapshotLabels(Guid[] Checking, Guid[] Rejected, Guid[] Waiting)
    {
        public static readonly ReturnSnapshotLabels None = new([], [], []);
    }

    // Ties have no order of their own: the latest invoice and package are taken by date, then by id, everywhere the return
    // screens read them (ReturnViewsAsync too), so the label filtered on is the label shown. Without snapshot labels the
    // claims the snapshot decides read SnapshotLabel; with them, their label (BILINMIYOR for a claim the read did not
    // cover: one whose snapshot changed in between).
    internal Expression<Func<ReturnClaim, string>> ReturnInvoiceLabel(Guid tenantId, ReturnSnapshotLabels? snapshotLabels)
    {
        var resolved = snapshotLabels is not null;
        var (checking, rejected, waiting) = snapshotLabels ?? ReturnSnapshotLabels.None;
        return claim => !db.Orders.Any(order => order.TenantId == tenantId && order.Id == claim.OrderId) ? "FATURA_BEKLIYOR"
            : db.Invoices.Where(invoice => invoice.TenantId == tenantId && invoice.OrderId == claim.OrderId && invoice.OriginalInvoiceId == null
                    && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == invoice.ProviderConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")))
                .OrderByDescending(invoice => invoice.CreatedAt).ThenByDescending(invoice => invoice.Id)
                .Select(invoice => invoice.Status == InvoiceStatus.UnknownResult ? "FATURA_BELIRSIZ"
                    : invoice.Status == InvoiceStatus.Rejected || invoice.Status == InvoiceStatus.ValidationFailed || invoice.Status == InvoiceStatus.ManualReview ? "FATURA_REDDEDILDI"
                    : invoice.Status == InvoiceStatus.MarketplaceFailed ? "FATURA_PLATFORMA_AKTARILMADI"
                    : invoice.Status == InvoiceStatus.Cancelled || invoice.Status == InvoiceStatus.CancelledLocal ? "FATURA_IPTAL"
                    : invoice.Status == InvoiceStatus.Accepted || invoice.Status == InvoiceStatus.MarketplacePending || invoice.Status == InvoiceStatus.Submitted || invoice.Status == InvoiceStatus.Submitting ? "FATURA_ISLENIYOR"
                    : invoice.Status == InvoiceStatus.Completed ? "FATURA_KONTROLDE"
                    : db.ShipmentPackages.Where(package => package.TenantId == tenantId && package.OrderId == claim.OrderId)
                        .OrderByDescending(package => package.StatusOccurredAt).ThenByDescending(package => package.Id)
                        .Select(package => package.MarketplaceInvoiceStatus == MarketplaceInvoiceStatus.Rejected ? "FATURA_REDDEDILDI"
                            : package.MarketplaceInvoiceStatus == MarketplaceInvoiceStatus.Received ? "FATURA_KONTROLDE"
                            : null)
                        .FirstOrDefault()
                        ?? (invoice.InvoiceNumber != null && !Regex.IsMatch(invoice.InvoiceNumber, BlankTextPattern)
                            && invoice.Status != InvoiceStatus.Draft && invoice.Status != InvoiceStatus.Validating && invoice.Status != InvoiceStatus.Ready ? "FATURA_KONTROLDE" : "FATURA_ISLENIYOR"))
                .FirstOrDefault()
            ?? db.ShipmentPackages.Where(package => package.TenantId == tenantId && package.OrderId == claim.OrderId)
                .OrderByDescending(package => package.StatusOccurredAt).ThenByDescending(package => package.Id)
                .Select(package => package.MarketplaceInvoiceStatus == MarketplaceInvoiceStatus.Invoiced || package.MarketplaceInvoiceStatus == MarketplaceInvoiceStatus.Received ? "FATURA_KONTROLDE"
                    : package.MarketplaceInvoiceStatus == MarketplaceInvoiceStatus.Rejected ? "FATURA_REDDEDILDI"
                    : package.MarketplaceInvoiceStatus == MarketplaceInvoiceStatus.NotInvoiced ? "FATURA_BEKLIYOR"
                    : package.RawStatus != null && Regex.IsMatch(package.RawStatus, InvoicedStatusPattern) ? "FATURA_KONTROLDE"
                    : null)
                .FirstOrDefault()
            ?? (!db.Orders.Any(order => order.TenantId == tenantId && order.Id == claim.OrderId && order.CustomerSnapshotJson != null && Regex.IsMatch(order.CustomerSnapshotJson, SnapshotInvoiceWordsPattern)) ? "FATURA_BILINMIYOR"
                : !resolved ? SnapshotLabel
                : checking.Contains(claim.Id) ? "FATURA_KONTROLDE"
                : rejected.Contains(claim.Id) ? "FATURA_REDDEDILDI"
                : waiting.Contains(claim.Id) ? "FATURA_BEKLIYOR"
                : "FATURA_BILINMIYOR");
    }

    // The labels JsonText can give a snapshot when nothing else decides (InvoiceLabel without an invoice, a marketplace
    // invoice status or an INVOICED package).
    private static readonly string[] SnapshotLabels = ["FATURA_KONTROLDE", "FATURA_REDDEDILDI", "FATURA_BEKLIYOR", "FATURA_BILINMIYOR"];

    // The exact label of the claims of this query, as a database expression: the claims the snapshot decides are read once
    // here (their snapshot, by JsonText) and their labels folded back in.
    internal async Task<Expression<Func<ReturnClaim, string>>> ReturnInvoiceLabelAsync(IQueryable<ReturnClaim> claims, Guid tenantId, CancellationToken cancellationToken)
    {
        var marked = ReturnInvoiceLabel(tenantId, null);
        var snapshots = await claims.Where(Is(marked, SnapshotLabel))
            .Select(claim => new { claim.Id, Snapshot = db.Orders.Where(order => order.TenantId == tenantId && order.Id == claim.OrderId).Select(order => order.CustomerSnapshotJson).FirstOrDefault() })
            .ToListAsync(cancellationToken);
        if (snapshots.Count == 0) return ReturnInvoiceLabel(tenantId, ReturnSnapshotLabels.None);
        var labels = snapshots.Select(row => (row.Id, Label: InvoiceLabel(null, null, MarketplaceInvoiceStatus.Unknown, row.Snapshot ?? "{}", []))).ToList();
        if (labels.Select(row => row.Label).FirstOrDefault(label => !SnapshotLabels.Contains(label)) is { } unexpected)
            throw new InvalidOperationException($"A customer snapshot gave the unexpected invoice label {unexpected}.");
        Guid[] Ids(string label) => [.. labels.Where(row => row.Label == label).Select(row => row.Id)];
        return ReturnInvoiceLabel(tenantId, new(Ids("FATURA_KONTROLDE"), Ids("FATURA_REDDEDILDI"), Ids("FATURA_BEKLIYOR")));
    }

    // The claims of the query whose invoice label is this one.
    internal async Task<IQueryable<ReturnClaim>> ApplyReturnInvoiceFilterAsync(IQueryable<ReturnClaim> query, Guid tenantId, string label, CancellationToken cancellationToken) =>
        query.Where(Is(await ReturnInvoiceLabelAsync(query, tenantId, cancellationToken), label));

    // claim => label(claim) == value, the value sent as a parameter.
    internal static Expression<Func<ReturnClaim, bool>> Is(Expression<Func<ReturnClaim, string>> label, string value) =>
        Expression.Lambda<Func<ReturnClaim, bool>>(Expression.Equal(label.Body, Expression.Property(Expression.Constant(new LabelValue(value)), nameof(LabelValue.Value))), label.Parameters);

    // claim => new ReturnLabelRow { Id = claim.Id, Label = label(claim) }.
    internal static Expression<Func<ReturnClaim, ReturnLabelRow>> LabelRow(Expression<Func<ReturnClaim, string>> label)
    {
        var claim = label.Parameters[0];
        return Expression.Lambda<Func<ReturnClaim, ReturnLabelRow>>(Expression.MemberInit(Expression.New(typeof(ReturnLabelRow)),
            Expression.Bind(typeof(ReturnLabelRow).GetProperty(nameof(ReturnLabelRow.Id))!, Expression.Property(claim, nameof(ReturnClaim.Id))),
            Expression.Bind(typeof(ReturnLabelRow).GetProperty(nameof(ReturnLabelRow.Label))!, label.Body)), claim);
    }

    private sealed record LabelValue(string Value);
}
