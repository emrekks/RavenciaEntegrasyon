using MarketplaceHub.Application;
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
    // Delivery state of a link that a cancelled invoice left on the marketplace. Nothing removes it on its own:
    // the operator removes a Trendyol link from the invoice detail ("Fatura Linkini Kaldır"), and removes the other
    // marketplaces' links in their seller panels (MarketplaceInvoiceLinkRevocationPolicy).
    public const string RevocationRequiredStatus = MarketplaceInvoiceLinkRevocationPolicy.Required;

    public static string AdjustmentIssueKey(Guid invoiceId) => $"invoice-adjustment:{invoiceId:N}";
    public static string LinkRevocationIssueKey(Guid invoiceId) => $"invoice-link-revocation:{invoiceId:N}";
    public static string ReturnDocumentIssueKey(Guid invoiceId) => $"invoice-return-document:{invoiceId:N}";

    /// <summary>
    /// Issued sale invoices whose order changed after issuing (owner's rules of 5 Nov 2026):
    /// - the order was cancelled, or every invoiced line was returned (approved): AdjustmentRequired as before
    ///   (e-Arşiv cancellation or a return invoice), which also stops the invoice's delivery;
    /// - only some lines were returned (an approved claim, or the approved lines of a claim decided in part): the
    ///   invoice keeps its status and is still delivered; the returned lines get their own note, "İade Belgesi
    ///   Gerekli", shown on the invoice. Nothing is cancelled and nothing is written to a provider or marketplace.
    /// </summary>
    public static async Task<int> FlagAdjustmentsAsync(AppDbContext db, Guid tenantId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var returnedStatuses = DashboardRevenuePolicy.ReturnedStatuses;
        var candidates = await (from invoice in db.Invoices
                                join order in db.Orders on new { invoice.TenantId, Id = invoice.OrderId } equals new { order.TenantId, order.Id }
                                where invoice.TenantId == tenantId
                                    && invoice.SequencePurpose == "SALE"
                                    && invoice.OriginalInvoiceId == null
                                    && invoice.ExternalReference != null
                                    && (invoice.Status == InvoiceStatus.Accepted || invoice.Status == InvoiceStatus.MarketplacePending
                                        || invoice.Status == InvoiceStatus.MarketplaceFailed || invoice.Status == InvoiceStatus.Completed
                                        || invoice.Status == InvoiceStatus.ManualReview)
                                    && (order.DerivedStatus == "CANCELLED" || order.DerivedStatus == "CANCELED"
                                        || returnedStatuses.Contains(order.DerivedStatus)
                                        || db.ReturnClaims.Any(claim => claim.TenantId == tenantId && claim.OrderId == order.Id && claim.Status != ReturnClaimStatus.Cancelled)
                                        // D-1: a line cancelled in part or returned on the marketplace (no claim needed), or a
                                        // package that came back, also changes what the invoice should hold.
                                        || db.OrderLines.Any(line => line.TenantId == tenantId && line.OrderId == order.Id && (line.CancelledQuantity > 0 || line.ReturnedQuantity > 0))
                                        || db.ShipmentPackages.Any(package => package.TenantId == tenantId && package.OrderId == order.Id && package.Status == ShipmentPackageStatus.Returned)
                                        || db.PackageLineAllocations.Any(allocation => allocation.TenantId == tenantId && allocation.ReturnedQuantity > 0
                                            && db.OrderLines.Any(line => line.TenantId == tenantId && line.Id == allocation.OrderLineId && line.OrderId == order.Id)))
                                select new { Invoice = invoice, order.Id, order.OrderNumber, order.DerivedStatus })
            .ToListAsync(cancellationToken);
        var flagged = 0;
        foreach (var candidate in candidates)
        {
            var invoice = candidate.Invoice;
            var cancelled = candidate.DerivedStatus is "CANCELLED" or "CANCELED";
            var scope = InvoiceReturnScope.None;
            IReadOnlyList<string> returnedTitles = [];
            decimal cancelledUnits = 0;
            if (!cancelled)
                (scope, returnedTitles, cancelledUnits) = await ReturnScopeAsync(db, tenantId, invoice.Id, candidate.Id, cancellationToken);
            if (cancelled || scope == InvoiceReturnScope.Full)
            {
                if (InvoiceStateMachine.CanTransition(invoice.Status, InvoiceStatus.AdjustmentRequired) && invoice.Status != InvoiceStatus.AdjustmentRequired)
                {
                    invoice.Status = InvoiceStatus.AdjustmentRequired;
                    invoice.LastErrorCode = AdjustmentRequiredCode;
                    invoice.UpdatedAt = now;
                    invoice.Version++;
                    var reason = cancelled ? "iptal edildi" : cancelledUnits > 0 ? "tüm ürünleri iptal ya da iade edildi" : "tüm ürünlerinin iadesi kabul edildi";
                    await UpsertIssueAsync(db, tenantId, AdjustmentIssueKey(invoice.Id), AdjustmentRequiredCode,
                        $"{candidate.OrderNumber} siparişi fatura kesildikten sonra {reason}. {invoice.InvoiceNumber ?? "Kesilen"} faturası için e-Arşiv iptali veya iade faturası gerekli.", now, cancellationToken);
                    await ResolveIssueAsync(db, tenantId, ReturnDocumentIssueKey(invoice.Id), now, cancellationToken);
                    flagged++;
                    continue;
                }
                // A failed delivery or an invoice in review cannot move to AdjustmentRequired; a full return still
                // gets its note so the owner sees it.
                if (cancelled) continue;
            }
            if (scope == InvoiceReturnScope.None) continue;
            var what = cancelledUnits > 0 ? $"{returnedTitles.Count} ürün iptal ya da iade edildi ({cancelledUnits.ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("tr-TR"))} adet iptal)" : $"{returnedTitles.Count} ürünün iadesi kabul edildi";
            var summary = $"{candidate.OrderNumber} siparişinde fatura kesildikten sonra {what}"
                + (returnedTitles.Count > 0 ? $" ({string.Join(", ", returnedTitles.Take(5))}{(returnedTitles.Count > 5 ? ", …" : string.Empty)})" : string.Empty)
                + $". {invoice.InvoiceNumber ?? "Kesilen"} faturası iptal edilmedi ve pazaryerine iletilmeye devam ediyor; iade edilen ürünler için iade belgesi (gider pusulası veya iade faturası) gerekli.";
            if (await UpsertIssueIfChangedAsync(db, tenantId, ReturnDocumentIssueKey(invoice.Id), InvoiceReturnAdjustmentPolicy.ReturnDocumentRequiredCode, summary, now, cancellationToken))
                flagged++;
        }
        return flagged;
    }

    // How much of the invoice the order no longer sells, the affected products' names and the units cancelled after
    // issuing. Returned units are read as for an invoice not issued yet (InvoiceSaleReturns.ReturnedUnitsAsync: the
    // order and package lines' returned quantities, a returned package or order, and approved claims; an open claim does
    // not count), and a line the marketplace cancelled in part after issuing counts its cancelled units (scan D-1).
    private static async Task<(InvoiceReturnScope Scope, IReadOnlyList<string> Titles, decimal CancelledUnits)> ReturnScopeAsync(AppDbContext db, Guid tenantId, Guid invoiceId, Guid orderId, CancellationToken cancellationToken)
    {
        var returned = await InvoiceSaleReturns.ReturnedUnitsAsync(db, tenantId, orderId, cancellationToken);
        var invoiceLines = await db.InvoiceLines.AsNoTracking()
            .Where(line => line.TenantId == tenantId && line.InvoiceId == invoiceId)
            .Select(line => new { line.OrderLineId, line.Quantity, line.DescriptionSnapshot })
            .ToListAsync(cancellationToken);
        var invoiced = invoiceLines.Where(line => line.OrderLineId != null)
            .GroupBy(line => line.OrderLineId!.Value)
            .ToDictionary(group => group.Key, group => group.Sum(line => line.Quantity));
        if (invoiced.Count == 0) return (InvoiceReturnScope.None, [], 0);
        var lineIds = invoiced.Keys.ToArray();
        var orderLines = await db.OrderLines.AsNoTracking()
            .Where(line => line.TenantId == tenantId && line.OrderId == orderId && lineIds.Contains(line.Id))
            .Select(line => new { line.Id, line.OrderedQuantity, line.CancelledQuantity })
            .ToDictionaryAsync(line => line.Id, cancellationToken);
        var removed = new Dictionary<Guid, decimal>();
        decimal cancelledUnits = 0;
        foreach (var (lineId, quantity) in invoiced)
        {
            if (!orderLines.TryGetValue(lineId, out var orderLine)) continue;
            cancelledUnits += InvoiceReturnAdjustmentPolicy.CancelledAfterIssue(quantity, orderLine.OrderedQuantity, orderLine.CancelledQuantity);
            var units = InvoiceReturnAdjustmentPolicy.RemovedUnits(quantity, orderLine.OrderedQuantity, orderLine.CancelledQuantity, returned.GetValueOrDefault(lineId));
            if (units > 0) removed[lineId] = units;
        }
        if (removed.Count == 0) return (InvoiceReturnScope.None, [], 0);
        var scope = InvoiceReturnAdjustmentPolicy.Classify(invoiced, removed, invoiceLines.Any(line => line.OrderLineId == null));
        var titles = invoiceLines.Where(line => line.OrderLineId is { } id && removed.ContainsKey(id))
            .Select(line => line.DescriptionSnapshot).Distinct(StringComparer.Ordinal).ToList();
        return (scope, titles, cancelledUnits);
    }

    // The note is written once and again only when it says something new (a scan every half hour would otherwise
    // count it up on every run).
    private static async Task<bool> UpsertIssueIfChangedAsync(AppDbContext db, Guid tenantId, string key, string code, string summary, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var issue = db.OperationalIssues.Local.FirstOrDefault(item => item.TenantId == tenantId && item.DedupeKey == key)
            ?? await db.OperationalIssues.SingleOrDefaultAsync(item => item.TenantId == tenantId && item.DedupeKey == key, cancellationToken);
        if (issue is not null && issue.Status == IssueStatus.Open && issue.Code == code && issue.Summary == summary) return false;
        await UpsertIssueAsync(db, tenantId, key, code, summary, now, cancellationToken);
        return true;
    }

    // The open "İade Belgesi Gerekli" notes of the given invoices, by invoice id (shown on the invoice detail and row).
    public static async Task<IReadOnlyDictionary<Guid, string>> ReturnDocumentNotesAsync(AppDbContext db, Guid tenantId, IReadOnlyCollection<Guid> invoiceIds, CancellationToken cancellationToken)
    {
        if (invoiceIds.Count == 0) return new Dictionary<Guid, string>();
        var keys = invoiceIds.Distinct().ToDictionary(ReturnDocumentIssueKey, id => id);
        var keyList = keys.Keys.ToArray();
        var issues = await db.OperationalIssues.AsNoTracking()
            .Where(issue => issue.TenantId == tenantId && keyList.Contains(issue.DedupeKey) && issue.Status != IssueStatus.Resolved)
            .Select(issue => new { issue.DedupeKey, issue.Summary })
            .ToListAsync(cancellationToken);
        return issues.ToDictionary(issue => keys[issue.DedupeKey], issue => issue.Summary);
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
        await CancelPendingDeliveryJobsAsync(db, invoice.TenantId, invoice.Id, now, cancellationToken);
        if (states.Count > 0)
            await UpsertIssueAsync(db, invoice.TenantId, LinkRevocationIssueKey(invoice.Id), LinkRevocationRequiredCode,
                $"{invoice.InvoiceNumber ?? "Fatura"} sağlayıcıda iptal edildi ancak pazaryerine iletilen fatura bağlantısı duruyor. Fatura detayındaki “Pazaryeri Fatura Linkleri” bölümünden kaldırın veya oradaki adımları izleyin; kaldırılmadan yeni fatura iletilemez.", now, cancellationToken);
        await ResolveIssueAsync(db, invoice.TenantId, AdjustmentIssueKey(invoice.Id), now, cancellationToken);
        return states.Count;
    }

    /// <summary>
    /// Cancels the invoice's marketplace delivery jobs that have not started (Pending or RetryScheduled) once the invoice
    /// is being cancelled or its links must come off. A leased job keeps its lease (the worker owns it); the delivery
    /// itself refuses a cancelled invoice and a state in link removal (InvoiceDeliveryGuardPolicy).
    /// </summary>
    public static async Task<int> CancelPendingDeliveryJobsAsync(AppDbContext db, Guid tenantId, Guid invoiceId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Every delivery job is keyed "{type}:{invoiceId}:..." (panel, automatic, recovery), so the key prefix finds them.
        var keyPrefix = $"{InvoicingJobTypes.MarketplaceDelivery}:{invoiceId}:";
        var jobs = await db.IntegrationJobs
            .Where(job => job.TenantId == tenantId && job.JobType == InvoicingJobTypes.MarketplaceDelivery
                && job.JobDedupKey.StartsWith(keyPrefix)
                && (job.Status == JobStatus.Pending || job.Status == JobStatus.RetryScheduled))
            .ToListAsync(cancellationToken);
        foreach (var job in jobs)
        {
            job.Status = JobStatus.Cancelled;
            job.CompletedAt = now;
            job.LastErrorCode = InvoiceDeliveryGuardPolicy.CancelledJobCode;
            job.LastErrorSummary = "Fatura iptal sürecine girdiği için pazaryeri teslimi durduruldu.";
            job.Version++;
        }
        return jobs.Count;
    }

    // Closes the link revocation issue once no link of the invoice is left to remove (tracked changes included).
    public static async Task ResolveLinkRevocationIfDoneAsync(AppDbContext db, Guid tenantId, Guid invoiceId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var states = await db.MarketplaceDeliveryStates.Where(state => state.TenantId == tenantId && state.InvoiceId == invoiceId).ToListAsync(cancellationToken);
        if (states.Any(state => MarketplaceInvoiceLinkRevocationPolicy.IsOpen(state.Status))) return;
        await ResolveIssueAsync(db, tenantId, LinkRevocationIssueKey(invoiceId), now, cancellationToken);
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
