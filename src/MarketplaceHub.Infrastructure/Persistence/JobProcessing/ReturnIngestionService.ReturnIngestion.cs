using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarketplaceHub.Infrastructure.Persistence;

internal sealed partial class ReturnIngestionService
{
    // The claim the seller marked received (MarkReturnReceivedAsync: Aksiyon Bekliyor, raw RECEIVED_BY_SELLER) while the
    // marketplace still reports a status from before the receipt.
    internal static bool AwaitsMarketplaceAfterSellerReceipt(string? storedRawStatus, ReturnClaimStatus target) =>
        string.Equals(storedRawStatus, "RECEIVED_BY_SELLER", StringComparison.Ordinal)
        && target is ReturnClaimStatus.Requested or ReturnClaimStatus.AwaitingShipment or ReturnClaimStatus.InTransit;

    private async Task UpsertReturnCore(Guid tenantId, Guid connectionId, RemoteReturnClaim remote, CancellationToken cancellationToken)
    {
        var remoteLines = remote.Lines.Count > 0 ? remote.Lines : TrendyolJsonMapper.ReturnLines(remote.RawJson);
        var claimLevelTarget = MarketplaceReturnStatus.Canonicalize(remote.RawStatus, remote.CargoTrackingLink);
        var remoteLineStatuses = remoteLines.Select(x => x.RawStatus).ToList();
        // A claim that mixes accepted lines with lines still waiting for the seller stays actionable; one whose lines
        // were decided both ways (accepted and rejected) is approved in part (ReturnClaimPartialApprovalPolicy).
        var target = ReturnLineStatusPolicy.Resolve(claimLevelTarget, remoteLineStatuses);
        var lineStatusOverrides = target != claimLevelTarget;
        var allLinesKnown = remoteLineStatuses.Count > 0 && remoteLineStatuses.All(x => !string.IsNullOrWhiteSpace(x));
        if (lineStatusOverrides && target == ReturnClaimStatus.ActionRequired) remote = remote with { RawStatus = "WaitingInAction" };
        if (!ReturnClaimStoragePolicy.ShouldPersist(target))
        {
            if (!await RemoveCancelledReturnClaim(tenantId, connectionId, remote, cancellationToken)) run.SkippedCount++;
            run.ReadReturnSkipReason = "Pazaryerinde kapanmış veya iptal edilmiş talep; panelde tutulmaz.";
            return;
        }

        var order = await db.Orders.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && (x.ExternalOrderId == remote.ExternalOrderId || x.OrderNumber == remote.ExternalOrderId), cancellationToken);
        if (order is null)
        {
            // Claims carry order-line identity even when the order falls
            // outside the order API history window. Reconstruct a minimal
            // local read model from the platform's own claim contract rather
            // than issuing a doomed remote lookup per claim.
            var platformCode = await ConnectionPlatformCode(tenantId, connectionId, cancellationToken);
            // Only Trendyol and Hepsiburada claims carry the order lines; an
            // n11/Pazarama claim would build a Trendyol-shaped empty order that
            // is never filled, so those are reported below and skipped.
            var claimOrder = platformCode switch
            {
                "HEPSIBURADA" => HepsiburadaJsonMapper.OrderFromReturnClaim(remote.RawJson),
                "TRENDYOL" => TrendyolJsonMapper.OrderFromReturnClaim(remote.RawJson),
                _ => null
            };
            if (claimOrder is not null)
            {
                // The claim already contains the product snapshot. Avoid
                // remote product lookups during a historical return scan.
                await UpsertOrder(tenantId, connectionId, claimOrder, cancellationToken);
                await ResolveIssue(tenantId, $"return-order:{connectionId}:{remote.ExternalOrderId}", cancellationToken);
                order = await db.Orders.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && (x.ExternalOrderId == remote.ExternalOrderId || x.OrderNumber == remote.ExternalOrderId), cancellationToken);
            }
        }
        if (order is null)
        {
            await RecordIssue(tenantId, $"return-order:{connectionId}:{remote.ExternalOrderId}", "RETURN_ORDER_NOT_FOUND", "Return claim yerel order ile eşleşmedi; sessiz kayıt oluşturulmadı.", cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            run.ReadReturnFailReason = "Bağlı sipariş panelde bulunamadı; talep kaydedilmedi.";
            return;
        }
        // A previous scan may have reconstructed the order after recording the
        // diagnostic. Resolve that stale diagnostic on the next successful read.
        await ResolveIssue(tenantId, $"return-order:{connectionId}:{remote.ExternalOrderId}", cancellationToken);
        var now = timeProvider.GetUtcNow(); var claim = await db.ReturnClaims.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalClaimId == remote.ExternalClaimId, cancellationToken);
        var storedStatusUnknown = claim is not null && (claim.Status == ReturnClaimStatus.Unknown || MarketplaceReturnStatus.Canonicalize(claim.RawStatus) == ReturnClaimStatus.Unknown);
        var remoteCargoProvider = string.IsNullOrWhiteSpace(remote.CargoProviderName) ? null : remote.CargoProviderName.Trim();
        var remoteCargoTracking = string.IsNullOrWhiteSpace(remote.CargoTrackingNumber) ? null : remote.CargoTrackingNumber.Trim();
        // Freshness is decided once for the whole read: an older snapshot than the one stored changes neither the
        // claim's status, its lines, its open decisions nor a pending "Stoğa Ekle"; only non-empty cargo facts merge.
        // Review, 18 Nov 2026: whether a Trendyol exchange claim carries the exchange package block at all (no content, no
        // customer data), so its live shape can be checked in the worker's log.
        if (remote.ReplacementInfoPresent is { } replacementPresent && remote.CustomerRequest == ReturnCustomerRequestPolicy.Change)
            (db.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance).CreateLogger<ReturnIngestionService>()
                .LogInformation("Exchange claim {ExternalClaimId} read: replacementOutboundpackageinfo present={Present}, package id={HasPackageId}", remote.ExternalClaimId, replacementPresent, remote.ReplacementPackage is not null);
        var remoteIsFresh = claim is null || !IsStaleReturnObservation(claim.LastRemoteModifiedAt, remote.LastModifiedAt);
        if (claim is null) { claim = NewReturnClaim(tenantId, connectionId, order.Id, remote, target, now); db.ReturnClaims.Add(claim); run.InsertedCount++; }
        else
        {
            var claimChanged = false;
            var previousRawStatus = claim.RawStatus;
            var previousClaimStatus = claim.Status;
            // A partial line decision waits for the lines' own statuses, except on a marketplace that decides the whole
            // claim (Shopify, Hepsiburada; ReturnDecisionScope): it never reports a line apart, so the claim follows what
            // the marketplace says on the next read instead of waiting forever (report item 8).
            var partialLineDecisionPending = string.Equals(claim.RawStatus, "PARTIAL_LINE_DECISION_PENDING", StringComparison.Ordinal) && !allLinesKnown
                && !ReturnDecisionScope.IsWholeClaim(await ConnectionPlatformCode(tenantId, connectionId, cancellationToken));
            if (remoteIsFresh && !partialLineDecisionPending && (lineStatusOverrides || ReturnClaimStateMachine.CanTransition(claim.Status, target)))
            {
                // CanTransition allows staying in the same status; a read that brings the same status, raw status and
                // change time is not an update (no Version or UpdatedAt bump).
                claimChanged = ReturnStatusObservationChanges(claim, target, remote.RawStatus, remote.LastModifiedAt);
                claim.Status = target;
                claim.RawStatus = remote.RawStatus;
                claim.LastRemoteModifiedAt = remote.LastModifiedAt ?? claim.LastRemoteModifiedAt;
            }
            else if (remoteIsFresh && !partialLineDecisionPending && AwaitsMarketplaceAfterSellerReceipt(claim.RawStatus, target))
            {
                // SIPARIS-STOK-1: the seller marked the return received (Teslim Aldım, a whole-claim marketplace) and the
                // marketplace still reports it as requested or on its way. That is not a refused transition: the claim
                // stays Aksiyon Bekliyor with its RECEIVED_BY_SELLER mark (so the next read does not raise it either),
                // and only the marketplace's change time is kept.
                if (remote.LastModifiedAt is not null && claim.LastRemoteModifiedAt != remote.LastModifiedAt)
                {
                    claim.LastRemoteModifiedAt = remote.LastModifiedAt;
                    claimChanged = true;
                }
            }
            else if (remoteIsFresh && !partialLineDecisionPending
                && (claim.Status != target || !string.Equals(claim.RawStatus, remote.RawStatus, StringComparison.Ordinal) || (remote.LastModifiedAt is not null && claim.LastRemoteModifiedAt != remote.LastModifiedAt)))
            {
                // A refused transition still records what the marketplace
                // reports, so the claim is not re-read as unchanged forever;
                // the marketplace's own outcome (approved, rejected, completed)
                // is authoritative even where the local workflow would refuse it.
                var authoritative = ReturnClaimStateMachine.IsAuthoritativeRemoteOutcome(claim.Status, target);
                var previousStatus = claim.Status;
                if (authoritative) claim.Status = target;
                claim.RawStatus = remote.RawStatus;
                claim.LastRemoteModifiedAt = remote.LastModifiedAt ?? claim.LastRemoteModifiedAt;
                claimChanged = true;
                if (previousStatus != target)
                    await RecordIssue(tenantId, $"return-transition:{claim.Id:N}:{Wire(target)}", "RETURN_STATUS_TRANSITION_REFUSED",
                        authoritative
                            ? $"{claim.ExternalClaimId} numaralı iade pazaryerinde {remote.RawStatus} olarak sonuçlandı; paneldeki {Wire(previousStatus)} durumu pazaryerine göre güncellendi."
                            : $"{claim.ExternalClaimId} numaralı iade için pazaryeri {remote.RawStatus} bildirdi ancak paneldeki {Wire(previousStatus)} durumundan bu geçiş yapılamıyor; ham durum güncellendi.",
                        cancellationToken);
            }
            if (remoteIsFresh)
            {
                if (!string.Equals(claim.ReasonCode, remote.ReasonCode, StringComparison.Ordinal)) { claim.ReasonCode = remote.ReasonCode; claimChanged = true; }
                if (!string.Equals(claim.ReasonText, remote.ReasonText, StringComparison.Ordinal)) { claim.ReasonText = remote.ReasonText; claimChanged = true; }
                var (dueAt, dueEstimated) = ReturnActionDuePolicy.Next(claim.ActionDueAt, claim.ActionDueAtEstimated, previousRawStatus, remote.ActionDueAt, remote.ActionDueAtEstimated, remote.RawStatus);
                if (claim.ActionDueAt != dueAt || claim.ActionDueAtEstimated != dueEstimated) { claim.ActionDueAt = dueAt; claim.ActionDueAtEstimated = dueEstimated; claimChanged = true; }
            }

            // Cargo may be returned by a historical/status replay with an old
            // or missing lastModifiedDate. Merge non-empty cargo independently
            // of the claim timestamp, but never replace known values with null.
            if (remoteCargoProvider is not null && !string.Equals(claim.CargoProviderName, remoteCargoProvider, StringComparison.Ordinal)) { claim.CargoProviderName = remoteCargoProvider; claimChanged = true; }
            if (remoteCargoTracking is not null && !string.Equals(claim.CargoTrackingNumber, remoteCargoTracking, StringComparison.Ordinal)) { claim.CargoTrackingNumber = remoteCargoTracking; claimChanged = true; }
            var remoteCargoTrackingLink = SafeReturnTrackingLink(remote.CargoTrackingLink);
            if (remoteCargoTrackingLink is not null && !string.Equals(claim.CargoTrackingLink, remoteCargoTrackingLink, StringComparison.Ordinal)) { claim.CargoTrackingLink = remoteCargoTrackingLink; claimChanged = true; }
            var (packageState, decidedAt) = NextReturnOutcomeFacts(claim.RejectedPackageState, claim.DecidedAt, previousClaimStatus, claim.Status, remote.RejectedPackageState, remote.DecidedAt);
            if (!string.Equals(claim.RejectedPackageState, packageState, StringComparison.Ordinal)) { claim.RejectedPackageState = packageState; claimChanged = true; }
            if (claim.DecidedAt != decidedAt) { claim.DecidedAt = decidedAt; claimChanged = true; }
            // The request date does not change with the claim's state, so any read that brings it fills or corrects it
            // (also an older snapshot); a read without it leaves the stored date.
            if (remote.RequestedAt?.ToUniversalTime() is { } requestedAt && claim.RequestedAt != requestedAt) { claim.RequestedAt = requestedAt; claimChanged = true; }
            // The refunded amount the marketplace reports (Hepsiburada refundAmount): any read that brings it sets it; a
            // read without it leaves the stored amount (unknown is never "nothing refunded").
            if (remote.RefundAmount is { } refundAmount && claim.RefundAmount != refundAmount) { claim.RefundAmount = refundAmount; claimChanged = true; }
            // What the customer asked for is kept once known; a read that does not say it leaves it as it is.
            if (remote.CustomerRequest is { } customerRequest && !string.Equals(claim.CustomerRequest, customerRequest, StringComparison.Ordinal)) { claim.CustomerRequest = customerRequest; claimChanged = true; }
            // The exchange package Trendyol names (getClaims replacementOutboundpackageinfo) is kept once read; a read without
            // it leaves what is known (unknown is never "no package").
            if (remote.ReplacementPackage is { } replacement && ApplyReplacementPackage(claim, replacement)) claimChanged = true;
            // How the claim was closed, as the marketplace says it (Hepsiburada's finalizedWith); a read that says nothing keeps it.
            if (remote.ResolvedWith is { } resolvedWith && (!string.Equals(claim.ResolvedWith, resolvedWith, StringComparison.Ordinal) || claim.ResolvedWithSource is not null))
            { claim.ResolvedWith = resolvedWith; claim.ResolvedWithSource = null; claimChanged = true; }
            // The owner's "Değişim Onaylandı" mark stands only on a rejected claim: one the marketplace reopened or accepted
            // loses it, and the owner is told (review, 18 Nov 2026).
            if (ReturnCustomerRequestPolicy.ManualMarkLapses(claim.ResolvedWithSource, claim.Status))
            {
                var shipMarkRemoved = ExchangeResendRules.ClearManualExchangeMark(claim); claimChanged = true;
                await RecordIssue(tenantId, $"return-exchange-mark-lapsed:{claim.Id:N}", "RETURN_EXCHANGE_MARK_LAPSED",
                    $"{claim.ExternalClaimId} numaralı iade elle Değişim Onaylandı olarak işaretlenmişti; pazaryeri iadeyi artık reddedilmiş göstermiyor ({remote.RawStatus}). İşaret kaldırıldı."
                        + (shipMarkRemoved ? ExchangeResendRules.ShipMarkAlsoRemovedText : ""), cancellationToken);
            }
            if (claimChanged) { claim.UpdatedAt = now; claim.Version++; run.UpdatedCount++; }
        }
        if (!remoteIsFresh)
        {
            // An older snapshot: its lines, its status word and its outcome are history the stored read already
            // replaced, so they neither rewrite a line nor settle a decision or a pending stock choice.
            await db.SaveChangesAsync(cancellationToken);
            return;
        }
        // A status word the panel does not know leaves the claim Unknown, without Onayla/Reddet (report item 9); the
        // issue tells the owner which word came, and the next read with a known word closes it.
        var unknownStatusIssueKey = $"return-status-unknown:{connectionId:N}:{remote.ExternalClaimId}";
        if (target == ReturnClaimStatus.Unknown)
            await RecordIssue(tenantId, unknownStatusIssueKey, "RETURN_STATUS_UNKNOWN",
                $"{remote.ExternalClaimId} numaralı iade için pazaryeri tanınmayan bir durum bildirdi ({(string.IsNullOrWhiteSpace(remote.RawStatus) ? "boş" : remote.RawStatus)}); iade \"Bilinmiyor\" olarak bırakıldı ve panelden işlem açılmadı. Kararı pazaryeri panelinden kontrol edin.", cancellationToken);
        else if (storedStatusUnknown)
            await ResolveIssue(tenantId, unknownStatusIssueKey, cancellationToken);
        var lineStatuses = new Dictionary<Guid, string?>();
        // The claim's open "line not linked" notices, read once per claim.
        var unlinkedPrefix = ReturnLineUnlinkedIssueKey(connectionId, claim.ExternalClaimId, "");
        var openUnlinkedIssues = (await db.OperationalIssues.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.Status != IssueStatus.Resolved && x.DedupeKey.StartsWith(unlinkedPrefix))
                .Select(x => x.DedupeKey).ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var remoteLine in remoteLines)
        {
            var candidateIds = new[] { remoteLine.ExternalOrderLineId }
                .Concat(remoteLine.AlternateExternalOrderLineIds ?? [])
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            OrderLine? orderLine = null;
            foreach (var candidateId in candidateIds)
            {
                orderLine = await db.OrderLines.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.OrderId == order.Id && x.ExternalLineId == candidateId, cancellationToken);
                if (orderLine is not null) break;
            }
            // A claimed line the order does not have is reported instead of dropped without a trace.
            var unlinkedIssueKey = ReturnLineUnlinkedIssueKey(connectionId, claim.ExternalClaimId, remoteLine.ExternalLineId);
            if (orderLine is null)
            {
                await RecordIssue(tenantId, unlinkedIssueKey, "RETURN_LINE_ORDER_LINE_NOT_FOUND",
                    candidateIds.Length == 0
                        ? $"{claim.ExternalClaimId} numaralı iadenin bir kaleminin siparişteki hangi kaleme ait olduğu bildirilmedi (doğrulanmamış iade kalemi); iade bu kalem olmadan kaydedildi."
                        : $"{claim.ExternalClaimId} numaralı iadenin bir kalemi siparişteki kalemlerle eşleşmedi; iade bu kalem olmadan kaydedildi.", cancellationToken);
                continue;
            }
            // The line now matches an order line (the order was read after the claim): the earlier "not linked" notice
            // no longer holds (audit 14 Nov 2026: five stayed open after their lines were linked).
            if (openUnlinkedIssues.Contains(unlinkedIssueKey)) await ResolveIssue(tenantId, unlinkedIssueKey, cancellationToken);
            var line = await db.ReturnLines.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ClaimId == claim.Id && x.ExternalLineId == remoteLine.ExternalLineId, cancellationToken);
            // A line whose returned quantity the marketplace did not say is not stored with a guessed quantity: an issue
            // is recorded, and a quantity read earlier is kept (report item 10).
            var quantityIssueKey = $"return-line-quantity:{connectionId:N}:{claim.ExternalClaimId}:{remoteLine.ExternalLineId}";
            if (remoteLine.Quantity <= 0)
            {
                await RecordIssue(tenantId, quantityIssueKey, "RETURN_LINE_QUANTITY_UNKNOWN",
                    $"{claim.ExternalClaimId} numaralı iadenin bir kaleminde adet bilinmiyor; pazaryeri iade adedini göndermedi. Kalem adetsiz kaydedilmedi, iade kararını pazaryeri panelinden kontrol edin.", cancellationToken);
                if (line is not null) lineStatuses[line.Id] = line.RawStatus;
                continue;
            }
            // The issue only stands for a line that was never stored; resolving it on the line's first store keeps the
            // lookup off every later read.
            if (line is null) await ResolveIssue(tenantId, quantityIssueKey, cancellationToken);
            if (line is null) db.ReturnLines.Add(line = new ReturnLine { Id = Guid.CreateVersion7(), TenantId = tenantId, ClaimId = claim.Id, OrderLineId = orderLine.Id, ExternalLineId = remoteLine.ExternalLineId, Quantity = remoteLine.Quantity, RawStatus = remoteLine.RawStatus, AcceptDetail = remoteLine.AcceptDetail, MarketplaceNote = remoteLine.Note, Resolved = remoteLine.Resolved, AcceptedBySeller = remoteLine.AcceptedBySeller }); else { line.Quantity = remoteLine.Quantity; if (!string.IsNullOrWhiteSpace(remoteLine.RawStatus)) line.RawStatus = remoteLine.RawStatus; line.AcceptDetail = remoteLine.AcceptDetail ?? line.AcceptDetail; line.MarketplaceNote = remoteLine.Note ?? line.MarketplaceNote; line.Resolved = remoteLine.Resolved ?? line.Resolved; line.AcceptedBySeller = remoteLine.AcceptedBySeller ?? line.AcceptedBySeller; }
            lineStatuses[line.Id] = line.RawStatus;
        }
        await ReconcileReturnDecisionAsync(tenantId, claim.Id, target, remote.RawStatus, lineStatuses, now, cancellationToken);
        // A "Stoğa Ekle" kept while the approval was unclear is applied once this read shows the lines approved, or
        // dropped when it shows them rejected; committed with the read, under the lines' locks.
        var claimId = claim.Id;
        await ReturnPendingStock.SettleAndSaveAsync(db, () => ReturnPendingStock.SettleClaimAsync(db, tenantId, claimId, $"return-stock:{claimId:N}", now, cancellationToken), cancellationToken);
        // Owner decision (6 Nov 2026): a Hepsiburada claim closed as an exchange sends the order's delivered invoice link again.
        if (await HepsiburadaExchangeInvoiceResend.QueueAsync(db, tenantId, claim,
                configuration.GetValue<bool>("FeatureFlags:ExternalWrites"),
                configuration.GetValue<bool>("FeatureFlags:InvoiceMarketplaceDeliveryWrites"),
                now, cancellationToken))
            await db.SaveChangesAsync(cancellationToken);
    }

    internal static string ReturnLineUnlinkedIssueKey(Guid connectionId, string externalClaimId, string externalLineId) =>
        $"return-line-unlinked:{connectionId}:{externalClaimId}:{externalLineId}";

    // A read whose marketplace change time is older than the one stored is stale. A read without a change time (or a
    // claim stored without one) cannot be called stale: unknown stays unknown, so it is taken as fresh.
    internal static bool IsStaleReturnObservation(DateTimeOffset? storedModifiedAt, DateTimeOffset? remoteModifiedAt) =>
        remoteModifiedAt is { } remoteAt && storedModifiedAt is { } storedAt && remoteAt < storedAt;

    // A decision whose outcome is still open: sent (PENDING/SUBMITTED),
    // waiting on an unclear marketplace answer (MANUAL_REVIEW with an
    // EXTERNAL_EFFECT_* code) or closed as unresolved, is settled by what the
    // marketplace reports. A decision that names its lines is settled by those
    // lines (the claim's other lines may still wait for their own decision);
    // one recorded before its lines were kept is settled by the claim status,
    // and only when it is the latest, as before.
    private async Task ReconcileReturnDecisionAsync(Guid tenantId, Guid claimId, ReturnClaimStatus target, string remoteRawStatus, IReadOnlyDictionary<Guid, string?> lineStatuses, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var decisions = await db.ReturnDecisions
            .Where(x => x.TenantId == tenantId && x.ClaimId == claimId
                && (x.Status == "PENDING" || x.Status == "SUBMITTED"
                    || x.Status == "MANUAL_REVIEW" && x.ErrorCode != null && x.ErrorCode.StartsWith("EXTERNAL_EFFECT_")
                    || x.Status == "FAILED" && x.ErrorCode == "EXTERNAL_EFFECT_UNRESOLVED"))
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken);
        for (var index = 0; index < decisions.Count; index++)
        {
            var decision = decisions[index];
            if (decision.ReturnLineIds is not { Length: > 0 } && index > 0) continue;
            var decisionTarget = decision.ReturnLineIds is { Length: > 0 } decisionLineIds
                ? ReturnDecisionLinePolicy.Outcome(target, decisionLineIds.Select(lineId => lineStatuses.GetValueOrDefault(lineId)).ToList())
                : target;
            if (ReconciledReturnDecision(decision.Action, decision.Status, decisionTarget, remoteRawStatus) is not { } outcome) continue;
            decision.Status = outcome.Status;
            decision.ErrorCode = outcome.ErrorCode;
            if (outcome.Status != "SUBMITTED") decision.CompletedAt = now;
        }
    }

    internal static (string Status, string? ErrorCode)? ReconciledReturnDecision(string action, string currentStatus, ReturnClaimStatus target, string remoteRawStatus)
    {
        var confirmed = action == "APPROVE" && target is ReturnClaimStatus.Approved or ReturnClaimStatus.Completed
            || action == "REJECT" && target is ReturnClaimStatus.Rejected or ReturnClaimStatus.Disputed
            || action == "PREAPPROVAL_CONFIRM" && (target is ReturnClaimStatus.AwaitingShipment or ReturnClaimStatus.InTransit
                || target == ReturnClaimStatus.ActionRequired && !HepsiburadaReturnActionPolicy.IsAwaitingPreApproval(remoteRawStatus));
        var conflictingTerminal = action == "APPROVE" && target is ReturnClaimStatus.Rejected or ReturnClaimStatus.Cancelled
            || action == "REJECT" && target is ReturnClaimStatus.Approved or ReturnClaimStatus.Completed
            || action == "PREAPPROVAL_CONFIRM" && target is ReturnClaimStatus.Approved or ReturnClaimStatus.Rejected or ReturnClaimStatus.Cancelled;
        if (confirmed) return ("SUCCEEDED", null);
        // An unresolved decision only ever moves on to a confirmed outcome.
        if (currentStatus == "FAILED") return null;
        if (conflictingTerminal) return ("MANUAL_REVIEW", "RETURN_ACTION_READBACK_CONFLICT");
        return currentStatus is "PENDING" or "SUBMITTED" ? ("SUBMITTED", null) : null;
    }

    // Returns true when the claim was kept as Cancelled because it has history.
    private async Task<bool> RemoveCancelledReturnClaim(Guid tenantId, Guid connectionId, RemoteReturnClaim remote, CancellationToken cancellationToken)
    {
        var stored = await db.ReturnClaims.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalClaimId == remote.ExternalClaimId)
            .Select(x => new { x.Id, x.LastRemoteModifiedAt })
            .SingleOrDefaultAsync(cancellationToken);
        if (stored is null) return false;
        // An older "cancelled" read than the stored one neither deletes the claim nor cancels it (RET-01).
        if (IsStaleReturnObservation(stored.LastRemoteModifiedAt, remote.LastModifiedAt)) return false;
        var claimId = stored.Id;
        var now = timeProvider.GetUtcNow();
        if (!await CancelledReturnClaimCleanup.RemoveOrRetainAsync(db, tenantId, claimId, remote.RawStatus, remote.LastModifiedAt, now, cancellationToken)) return false;
        await ReconcileReturnDecisionAsync(tenantId, claimId, ReturnClaimStatus.Cancelled, remote.RawStatus, new Dictionary<Guid, string?>(), now, cancellationToken);
        await ReturnPendingStock.SettleAndSaveAsync(db, () => ReturnPendingStock.SettleClaimAsync(db, tenantId, claimId, $"return-stock:{claimId:N}", now, cancellationToken), cancellationToken);
        return true;
    }

    // Whether a read of a claim says anything new about its status: another status, raw status or marketplace change time.
    internal static bool ReturnStatusObservationChanges(ReturnClaim claim, ReturnClaimStatus target, string remoteRawStatus, DateTimeOffset? remoteLastModifiedAt) =>
        claim.Status != target
        || !string.Equals(claim.RawStatus, remoteRawStatus, StringComparison.Ordinal)
        || (remoteLastModifiedAt is not null && claim.LastRemoteModifiedAt != remoteLastModifiedAt);

    internal static ReturnClaim NewReturnClaim(Guid tenantId, Guid connectionId, Guid orderId, RemoteReturnClaim remote, ReturnClaimStatus status, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(),
        TenantId = tenantId,
        ConnectionId = connectionId,
        OrderId = orderId,
        ExternalClaimId = remote.ExternalClaimId,
        Status = status,
        RawStatus = remote.RawStatus,
        CargoProviderName = string.IsNullOrWhiteSpace(remote.CargoProviderName) ? null : remote.CargoProviderName.Trim(),
        CargoTrackingNumber = string.IsNullOrWhiteSpace(remote.CargoTrackingNumber) ? null : remote.CargoTrackingNumber.Trim(),
        CargoTrackingLink = SafeReturnTrackingLink(remote.CargoTrackingLink),
        ReasonCode = remote.ReasonCode,
        ReasonText = remote.ReasonText,
        ActionDueAt = remote.ActionDueAt,
        ActionDueAtEstimated = remote.ActionDueAtEstimated,
        LastRemoteModifiedAt = remote.LastModifiedAt,
        RejectedPackageState = status == ReturnClaimStatus.Rejected ? remote.RejectedPackageState : null,
        DecidedAt = status is ReturnClaimStatus.Approved or ReturnClaimStatus.Rejected ? remote.DecidedAt?.ToUniversalTime() : null,
        CustomerRequest = remote.CustomerRequest,
        ReplacementPackageId = remote.ReplacementPackage?.PackageId,
        ReplacementCargoProviderName = remote.ReplacementPackage?.CargoProviderName,
        ReplacementCargoTrackingNumber = remote.ReplacementPackage?.CargoTrackingNumber,
        ReplacementCargoTrackingLink = SafeReturnTrackingLink(remote.ReplacementPackage?.CargoTrackingLink),
        ResolvedWith = remote.ResolvedWith,
        RequestedAt = remote.RequestedAt?.ToUniversalTime(),
        RefundAmount = remote.RefundAmount,
        CreatedAt = now,
        UpdatedAt = now,
        Version = 1
    };

    // The rejected package state and the decision date after a read. A new outcome drops the old outcome's date (a
    // Trendyol claim is dated again from its audits); a date the read brings is kept only for an approved or rejected
    // claim. A package that left (or is never sent back) does not go back to waiting on an older read.
    internal static (string? RejectedPackageState, DateTimeOffset? DecidedAt) NextReturnOutcomeFacts(
        string? currentPackageState, DateTimeOffset? currentDecidedAt, ReturnClaimStatus previousStatus, ReturnClaimStatus status, string? remotePackageState, DateTimeOffset? remoteDecidedAt)
    {
        var packageState = status != ReturnClaimStatus.Rejected ? null
            : remotePackageState is null || !ReturnRejectedPackageStates.IsOpen(currentPackageState) && ReturnRejectedPackageStates.IsOpen(remotePackageState) ? currentPackageState
            : remotePackageState;
        var decidedAt = status is not (ReturnClaimStatus.Approved or ReturnClaimStatus.Rejected) ? null
            : remoteDecidedAt is { } remoteDate ? remoteDate.ToUniversalTime()
            : previousStatus == status ? currentDecidedAt
            : null;
        return (packageState, decidedAt);
    }

    // Writes the exchange package a read names; true when something changed. A carrier field the read leaves out keeps
    // its stored value.
    internal static bool ApplyReplacementPackage(ReturnClaim claim, RemoteReplacementPackage replacement)
    {
        var link = SafeReturnTrackingLink(replacement.CargoTrackingLink);
        var (provider, tracking) = (replacement.CargoProviderName?.Trim(), replacement.CargoTrackingNumber?.Trim());
        var changed = false;
        if (claim.ReplacementPackageId != replacement.PackageId) { claim.ReplacementPackageId = replacement.PackageId; changed = true; }
        if (!string.IsNullOrEmpty(provider) && claim.ReplacementCargoProviderName != provider) { claim.ReplacementCargoProviderName = provider; changed = true; }
        if (!string.IsNullOrEmpty(tracking) && claim.ReplacementCargoTrackingNumber != tracking) { claim.ReplacementCargoTrackingNumber = tracking; changed = true; }
        if (link is not null && claim.ReplacementCargoTrackingLink != link) { claim.ReplacementCargoTrackingLink = link; changed = true; }
        return changed;
    }
}
