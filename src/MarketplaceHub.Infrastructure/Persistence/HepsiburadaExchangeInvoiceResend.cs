using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

/// <summary>
/// Owner decision (6 Nov 2026, ExchangeInvoiceResendPolicy): a Hepsiburada claim closed as an exchange queues one
/// INVOICE_MARKETPLACE_DELIVERY job that sends the order's already delivered invoice link to Hepsiburada again (the same
/// document, no new invoice, no cancellation, no E-Faturam call). One job per claim, kept by its dedup key; a package
/// that is not known for certain, or a closed invoice delivery switch, sends nothing and leaves a note for the owner.
/// </summary>
public static class HepsiburadaExchangeInvoiceResend
{
    // Returns true when something was added (a job or a note) and must be saved.
    public static async Task<bool> QueueAsync(AppDbContext db, Guid tenantId, ReturnClaim claim, bool externalWritesEnabled, bool invoiceDeliveryWritesEnabled, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Cheap checks first: this runs on every read of every claim.
        if (claim.Status is not (ReturnClaimStatus.Approved or ReturnClaimStatus.Completed)) return false;
        if (claim.ResolvedWith == ReturnCustomerRequestPolicy.Refund) return false;
        var platformCode = await db.PlatformConnections.AsNoTracking()
            .Where(connection => connection.TenantId == tenantId && connection.Id == claim.ConnectionId)
            .Select(connection => connection.PlatformCode)
            .SingleOrDefaultAsync(cancellationToken);
        if (!string.Equals(platformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase)) return false;

        // The claim's own finalizedWith decides; only when it does not say, the panel's own approval that Hepsiburada
        // confirmed (its FinalizedWith is kept as the decision's ReasonCode) speaks for it.
        var panelApprovedWith = claim.ResolvedWith is null
            ? await db.ReturnDecisions.AsNoTracking()
                .Where(decision => decision.TenantId == tenantId && decision.ClaimId == claim.Id && decision.Action == "APPROVE" && decision.Status == "SUCCEEDED")
                .OrderByDescending(decision => decision.CreatedAt)
                .Select(decision => decision.ReasonCode)
                .FirstOrDefaultAsync(cancellationToken)
            : null;
        if (!ExchangeInvoiceResendPolicy.ClosedAsExchange(claim.Status, claim.RawStatus, claim.ResolvedWith, panelApprovedWith)) return false;

        var jobKey = ExchangeInvoiceResendPolicy.JobKey(claim.Id);
        if (db.IntegrationJobs.Local.Any(job => job.TenantId == tenantId && job.JobType == InvoicingJobTypes.MarketplaceDelivery && job.JobDedupKey == jobKey)
            || await db.IntegrationJobs.AsNoTracking().AnyAsync(job => job.TenantId == tenantId && job.JobType == InvoicingJobTypes.MarketplaceDelivery && job.JobDedupKey == jobKey, cancellationToken))
            return false;

        // The order's invoice links Hepsiburada confirmed, of sale invoices that are still valid and have a document link.
        var delivered = await (from state in db.MarketplaceDeliveryStates.AsNoTracking()
                               join invoice in db.Invoices.AsNoTracking() on new { state.TenantId, Id = state.InvoiceId } equals new { invoice.TenantId, invoice.Id }
                               join package in db.ShipmentPackages.AsNoTracking() on new { state.TenantId, Id = state.PackageId!.Value } equals new { package.TenantId, package.Id }
                               where state.TenantId == tenantId
                                   && state.Status == "CONFIRMED"
                                   && invoice.OrderId == claim.OrderId
                                   && invoice.SequencePurpose == "SALE"
                                   && invoice.InvoiceNumber != null
                                   && invoice.Status != InvoiceStatus.Cancelled && invoice.Status != InvoiceStatus.CancellationPending
                                   && invoice.Status != InvoiceStatus.CancellationRejected && invoice.Status != InvoiceStatus.AdjustmentRequired
                                   && invoice.Status != InvoiceStatus.CancelledLocal && invoice.Status != InvoiceStatus.Rejected
                                   && package.ConnectionId == claim.ConnectionId
                                   && db.InvoiceDocuments.Any(document => document.TenantId == tenantId && document.InvoiceId == invoice.Id && document.PermanentUrl != null)
                               select new { InvoiceId = invoice.Id, invoice.InvoiceNumber, PackageId = package.Id, package.ExternalPackageId })
            .ToListAsync(cancellationToken);
        // No invoice yet, or not delivered yet: the normal invoicing and delivery apply later; nothing to send again.
        if (delivered.Count == 0) return false;

        var orderNumber = await db.Orders.AsNoTracking().Where(order => order.TenantId == tenantId && order.Id == claim.OrderId).Select(order => order.OrderNumber).SingleOrDefaultAsync(cancellationToken);
        var claimLineIds = await db.ReturnLines.AsNoTracking().Where(line => line.TenantId == tenantId && line.ClaimId == claim.Id).Select(line => line.OrderLineId).ToListAsync(cancellationToken);
        var claimPackages = claimLineIds.Count == 0 ? [] : await db.PackageLineAllocations.AsNoTracking()
            .Where(allocation => allocation.TenantId == tenantId && claimLineIds.Contains(allocation.OrderLineId))
            .Select(allocation => allocation.PackageId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var targetPackage = ExchangeInvoiceResendPolicy.TargetPackage(delivered.Select(item => item.PackageId).ToList(), claimPackages);
        var targets = targetPackage is { } packageId ? delivered.Where(item => item.PackageId == packageId).DistinctBy(item => item.InvoiceId).ToList() : [];
        var issueKey = ExchangeInvoiceResendPolicy.IssueKey(claim.Id);
        if (targets.Count != 1)
        {
            return await NoteAsync(db, tenantId, issueKey, ExchangeInvoiceResendPolicy.PackageUnknownCode,
                $"Hepsiburada {claim.ExternalClaimId} numaralı talep değişimle onaylandı (sipariş {orderNumber}); mevcut faturanın hangi pakete yeniden gönderileceği bilinmiyor, fatura gönderilmedi. Hepsiburada panelinden kontrol edin.",
                now, cancellationToken);
        }

        var target = targets[0];
        if (!IntegrationRuntimePolicy.AllowsAutomaticInvoiceMarketplaceDeliveryQueue(externalWritesEnabled, invoiceDeliveryWritesEnabled))
        {
            return await NoteAsync(db, tenantId, issueKey, ExchangeInvoiceResendPolicy.DeliveryDisabledCode,
                $"Hepsiburada {claim.ExternalClaimId} numaralı talep değişimle onaylandı (sipariş {orderNumber}, fatura {target.InvoiceNumber}, paket {target.ExternalPackageId}); pazaryerine fatura iletme kapalı olduğu için fatura yeniden gönderilmedi.",
                now, cancellationToken);
        }

        var payload = JsonSerializer.Serialize(new { invoiceId = target.InvoiceId, packageId = target.PackageId, exchangeResendClaimId = claim.Id });
        db.IntegrationJobs.Add(new IntegrationJob
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ConnectionId = claim.ConnectionId,
            JobType = InvoicingJobTypes.MarketplaceDelivery,
            PayloadJson = payload,
            PayloadVersion = 1,
            PayloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))),
            JobDedupKey = jobKey,
            EffectIdempotencyKey = jobKey,
            Priority = JobPriorityPolicy.ForFollowUp(null),
            TriggerType = "system",
            AvailableAt = now,
            CorrelationId = $"hb-exchange:{claim.Id:N}",
            Version = 1
        });
        // A note left while the package or the switch stood in the way is settled by the queued job.
        var stale = db.OperationalIssues.Local.FirstOrDefault(issue => issue.TenantId == tenantId && issue.DedupeKey == issueKey)
            ?? await db.OperationalIssues.SingleOrDefaultAsync(issue => issue.TenantId == tenantId && issue.DedupeKey == issueKey, cancellationToken);
        if (stale is not null && stale.Status != IssueStatus.Resolved)
        {
            stale.Status = IssueStatus.Resolved;
            stale.ResolvedAt = now;
        }
        return true;
    }

    // Opens (or refreshes) the claim's note for the owner; false when the same note is already open.
    public static async Task<bool> NoteAsync(AppDbContext db, Guid tenantId, string key, string code, string summary, DateTimeOffset now, CancellationToken cancellationToken, bool resolved = false)
    {
        var issue = db.OperationalIssues.Local.FirstOrDefault(item => item.TenantId == tenantId && item.DedupeKey == key)
            ?? await db.OperationalIssues.SingleOrDefaultAsync(item => item.TenantId == tenantId && item.DedupeKey == key, cancellationToken);
        var status = resolved ? IssueStatus.Resolved : IssueStatus.Open;
        if (issue is not null && issue.Status == status && issue.Code == code && issue.Summary == summary) return false;
        if (issue is null)
        {
            db.OperationalIssues.Add(new OperationalIssue { Id = Guid.CreateVersion7(), TenantId = tenantId, DedupeKey = key, Code = code, Summary = summary, Status = status, FirstSeenAt = now, LastSeenAt = now, OccurrenceCount = 1, ResolvedAt = resolved ? now : null });
            return true;
        }
        issue.Code = code;
        issue.Summary = summary;
        issue.Status = status;
        issue.LastSeenAt = now;
        issue.ResolvedAt = resolved ? now : null;
        issue.OccurrenceCount++;
        return true;
    }
}
