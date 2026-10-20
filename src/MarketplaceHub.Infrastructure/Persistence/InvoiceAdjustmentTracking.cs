using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

/// <summary>
/// Follow-up work once a sale invoice no longer matches the order: the order was cancelled or a return was
/// accepted after issuing, or the invoice was cancelled at the provider while the marketplace still holds
/// its link. Nothing here writes to a provider or marketplace; it records what the operator must do.
/// </summary>
internal static class InvoiceAdjustmentTracking
{
    public const string AdjustmentRequiredCode = "INVOICE_ADJUSTMENT_REQUIRED";
    public const string LinkRevocationRequiredCode = "INVOICE_MARKETPLACE_LINK_REVOCATION_REQUIRED";
    // Delivery state of a link that a cancelled invoice left on the marketplace. No adapter can remove a
    // Trendyol/Hepsiburada invoice link yet, so it is removed in the seller panel.
    public const string RevocationRequiredStatus = "REVOCATION_REQUIRED";

    public static string AdjustmentIssueKey(Guid invoiceId) => $"invoice-adjustment:{invoiceId:N}";
    public static string LinkRevocationIssueKey(Guid invoiceId) => $"invoice-link-revocation:{invoiceId:N}";

    /// <summary>Moves issued sale invoices of cancelled orders or accepted returns to AdjustmentRequired.</summary>
    public static async Task<int> FlagAdjustmentsAsync(AppDbContext db, Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var candidates = await (from invoice in db.Invoices
                                join order in db.Orders on new { invoice.TenantId, Id = invoice.OrderId } equals new { order.TenantId, order.Id }
                                where invoice.TenantId == tenantId
                                    && invoice.SequencePurpose == "SALE"
                                    && invoice.OriginalInvoiceId == null
                                    && (invoice.Status == InvoiceStatus.Accepted || invoice.Status == InvoiceStatus.MarketplacePending || invoice.Status == InvoiceStatus.Completed)
                                    && (order.DerivedStatus == "CANCELLED" || order.DerivedStatus == "CANCELED"
                                        || db.ReturnClaims.Any(claim => claim.TenantId == tenantId && claim.OrderId == order.Id
                                            && (claim.Status == ReturnClaimStatus.Approved || claim.Status == ReturnClaimStatus.Completed)))
                                select new { Invoice = invoice, order.OrderNumber, order.DerivedStatus })
            .ToListAsync(cancellationToken);
        foreach (var candidate in candidates)
        {
            var invoice = candidate.Invoice;
            if (!InvoiceStateMachine.CanTransition(invoice.Status, InvoiceStatus.AdjustmentRequired)) continue;
            invoice.Status = InvoiceStatus.AdjustmentRequired;
            invoice.LastErrorCode = AdjustmentRequiredCode;
            invoice.UpdatedAt = now;
            invoice.Version++;
            var reason = candidate.DerivedStatus is "CANCELLED" or "CANCELED" ? "iptal edildi" : "iadesi kabul edildi";
            await UpsertIssueAsync(db, tenantId, AdjustmentIssueKey(invoice.Id), AdjustmentRequiredCode,
                $"{candidate.OrderNumber} siparişi fatura kesildikten sonra {reason}. {invoice.InvoiceNumber ?? "Kesilen"} faturası için e-Arşiv iptali veya iade faturası gerekli.", now, cancellationToken);
        }
        return candidates.Count;
    }

    /// <summary>
    /// After the provider cancelled an invoice, every marketplace link that was sent (or may have been sent)
    /// for it must be removed; until then a new invoice is blocked by the marketplace precheck.
    /// </summary>
    public static async Task<int> MarkLinkRevocationRequiredAsync(AppDbContext db, Invoice invoice, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var states = await db.MarketplaceDeliveryStates
            .Where(state => state.TenantId == invoice.TenantId && state.InvoiceId == invoice.Id
                && (state.Status == "CONFIRMED" || state.Status == "SUBMITTED" || state.Status == "CONFIRMATION_RETRYABLE" || state.Status == "UNKNOWN" || state.Status == "STARTED"))
            .ToListAsync(cancellationToken);
        foreach (var state in states)
            AppendDeliveryHistory(db, state, RevocationRequiredStatus, state.ExternalReference, LinkRevocationRequiredCode, now);
        if (states.Count > 0)
            await UpsertIssueAsync(db, invoice.TenantId, LinkRevocationIssueKey(invoice.Id), LinkRevocationRequiredCode,
                $"{invoice.InvoiceNumber ?? "Fatura"} sağlayıcıda iptal edildi ancak pazaryerine iletilen fatura bağlantısı duruyor. Bağlantıyı pazaryeri satıcı panelinden kaldırın; kaldırılmadan yeni fatura iletilemez.", now, cancellationToken);
        await ResolveIssueAsync(db, invoice.TenantId, AdjustmentIssueKey(invoice.Id), now, cancellationToken);
        return states.Count;
    }

    public static MarketplaceDelivery AppendDeliveryHistory(AppDbContext db, MarketplaceDeliveryState state, string status, string? externalReference, string? errorCode, DateTimeOffset now)
    {
        var sequence = state.AttemptNumber + 1;
        var entry = new MarketplaceDelivery
        {
            Id = Guid.CreateVersion7(),
            TenantId = state.TenantId,
            InvoiceId = state.InvoiceId,
            ConnectionId = state.ConnectionId,
            PackageId = state.PackageId,
            AttemptNumber = sequence,
            IdempotencyKey = $"delivery-event:{state.Id:N}:{sequence}",
            ExternalIdempotencyKey = state.ExternalIdempotencyKey,
            RequestHash = state.RequestHash,
            DeliveryType = state.DeliveryType,
            Status = status,
            ExternalReference = externalReference ?? state.ExternalReference,
            ErrorCode = errorCode,
            CreatedAt = now,
            CompletedAt = status == "STARTED" ? null : now
        };
        db.MarketplaceDeliveries.Add(entry);
        state.AttemptNumber = sequence;
        state.Status = status;
        state.ExternalReference = entry.ExternalReference;
        state.ErrorCode = errorCode;
        state.CompletedAt = status is "FAILED" or "CONFIRMED" ? now : null;
        state.UpdatedAt = now;
        state.Version++;
        return entry;
    }

    private static async Task UpsertIssueAsync(AppDbContext db, Guid tenantId, string key, string code, string summary, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var issue = db.OperationalIssues.Local.FirstOrDefault(item => item.TenantId == tenantId && item.DedupeKey == key)
            ?? await db.OperationalIssues.SingleOrDefaultAsync(item => item.TenantId == tenantId && item.DedupeKey == key, cancellationToken);
        if (issue is null)
        {
            db.OperationalIssues.Add(new OperationalIssue { Id = Guid.CreateVersion7(), TenantId = tenantId, DedupeKey = key, Code = code, Summary = summary, Status = IssueStatus.Open, FirstSeenAt = now, LastSeenAt = now, OccurrenceCount = 1 });
            return;
        }
        issue.Code = code;
        issue.Summary = summary;
        issue.Status = IssueStatus.Open;
        issue.LastSeenAt = now;
        issue.OccurrenceCount++;
    }

    private static async Task ResolveIssueAsync(AppDbContext db, Guid tenantId, string key, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var issue = await db.OperationalIssues.SingleOrDefaultAsync(item => item.TenantId == tenantId && item.DedupeKey == key && item.Status != IssueStatus.Resolved, cancellationToken);
        if (issue is null) return;
        issue.Status = IssueStatus.Resolved;
        issue.LastSeenAt = now;
    }
}
