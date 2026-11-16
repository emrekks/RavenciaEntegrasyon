using System.Security.Cryptography;
using System.Text;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed partial class MarketplaceJobProcessor
{
    private async Task UpsertShopifyFinancialObservations(Guid tenantId, Guid orderId, RemoteOrder remote, CancellationToken cancellationToken)
    {
        var rows = await db.OrderFinancialAllocations
            .Where(x => x.TenantId == tenantId && x.OrderId == orderId && x.SourceKey.StartsWith("shopify:"))
            .ToListAsync(cancellationToken);
        var byKey = rows.ToDictionary(x => x.SourceKey, StringComparer.Ordinal);

        Upsert("shopify:payment-status", $"PAYMENT_STATUS:{Short(remote.PaymentStatus, 48)}", 0m);
        Upsert("shopify:cancellation-status", $"CANCELLATION_STATUS:{Short(remote.CancellationStatus, 48)}", 0m);
        Upsert("shopify:refund-summary", $"REFUND_STATUS:{Short(remote.RefundStatus, 48)}", remote.RefundedAmount);

        var currentRefundKeys = (remote.Refunds ?? [])
            .Where(refund => !string.IsNullOrWhiteSpace(refund.ExternalRefundId))
            .Select(refund => $"shopify:refund:{Short(refund.ExternalRefundId, 180)}")
            .ToHashSet(StringComparer.Ordinal);
        foreach (var stale in rows.Where(row => row.SourceKey.StartsWith("shopify:refund:") && row.SourceKey != "shopify:refund-summary" && !currentRefundKeys.Contains(row.SourceKey)))
            db.OrderFinancialAllocations.Remove(stale);
        foreach (var refund in remote.Refunds ?? [])
        {
            if (string.IsNullOrWhiteSpace(refund.ExternalRefundId)) continue;
            Upsert($"shopify:refund:{Short(refund.ExternalRefundId, 180)}", "REFUND", refund.Amount, refund.Currency);
        }
        return;

        void Upsert(string sourceKey, string allocationType, decimal amount, string? currency = null)
        {
            if (byKey.TryGetValue(sourceKey, out var row))
            {
                row.AllocationType = allocationType;
                row.Amount = Math.Max(0, amount);
                row.Currency = Short(currency ?? remote.Currency, 3).ToUpperInvariant();
                return;
            }
            db.OrderFinancialAllocations.Add(new OrderFinancialAllocation
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                OrderId = orderId,
                AllocationType = allocationType,
                Amount = Math.Max(0, amount),
                Currency = Short(currency ?? remote.Currency, 3).ToUpperInvariant(),
                SourceKey = sourceKey
            });
        }
    }

    // discoverySource ("stream" or "reconciliation"), when given, counts which path first saw a
    // Trendyol package become invoiced (plan step 0.9: do invoices uploaded outside the panel reach
    // the order stream, or only the reconciliation sweep?).
    private async Task MergeMarketplaceInvoiceState(ShipmentPackage package, RemotePackage remotePackage, CancellationToken cancellationToken, string? discoverySource = null)
    {
        var wasInvoiced = package.MarketplaceInvoiceStatus == MarketplaceInvoiceStatus.Invoiced;
        var observedAt = timeProvider.GetUtcNow();
        var observation = remotePackage.Invoice;
        var incomingStatus = MarketplaceInvoiceStatePolicy.FromRemote(
            observation?.RawStatus,
            remotePackage.RawStatus,
            observation?.InvoiceNumber,
            observation?.InvoiceUrl);
        var applyStatus = MarketplaceInvoiceStatePolicy.ShouldApply(
                package.MarketplaceInvoiceStatus,
                package.MarketplaceInvoiceSourceUpdatedAt,
                package.MarketplaceInvoiceObservedAt,
                incomingStatus,
                observation?.SourceUpdatedAt,
                observedAt);
        var applyReferences = incomingStatus != MarketplaceInvoiceStatus.Unknown
            && MarketplaceInvoiceStatePolicy.ShouldApplyReferenceUpdate(
            package.MarketplaceInvoiceSourceUpdatedAt,
            package.MarketplaceInvoiceObservedAt,
            observation?.SourceUpdatedAt,
            observedAt);
        var referenceChanged = false;
        if (applyReferences && incomingStatus == MarketplaceInvoiceStatus.NotInvoiced)
        {
            referenceChanged = package.MarketplaceInvoiceNumber is not null || package.MarketplaceInvoiceUrl is not null;
            if (referenceChanged)
                MarketplaceInvoiceReferenceHistoryRecorder.RecordPreviousReferences(db, package, null, null, observedAt, "ORDER_RECONCILIATION");
            package.MarketplaceInvoiceNumber = null;
            package.MarketplaceInvoiceUrl = null;
        }
        else if (applyReferences)
        {
            var references = MarketplaceInvoiceStatePolicy.MergeReferences(
                package.MarketplaceInvoiceNumber,
                package.MarketplaceInvoiceUrl,
                observation?.InvoiceNumber,
                observation?.InvoiceUrl,
                observation?.ReferencesAreAuthoritative == true);
            var invoiceNumber = references.InvoiceNumber;
            var invoiceUrl = references.InvoiceUrl;
            referenceChanged = !string.Equals(invoiceNumber, package.MarketplaceInvoiceNumber, StringComparison.Ordinal)
                || !string.Equals(invoiceUrl, package.MarketplaceInvoiceUrl, StringComparison.Ordinal);
            if (referenceChanged)
                MarketplaceInvoiceReferenceHistoryRecorder.RecordPreviousReferences(db, package, invoiceNumber, invoiceUrl, observedAt, "ORDER_RECONCILIATION");
            package.MarketplaceInvoiceNumber = invoiceNumber;
            package.MarketplaceInvoiceUrl = invoiceUrl;
        }

        if (applyStatus)
        {
            package.MarketplaceInvoiceStatus = incomingStatus;
            package.MarketplaceInvoiceRawStatus = observation?.RawStatus ?? package.MarketplaceInvoiceRawStatus;
            package.MarketplaceInvoiceSourceUpdatedAt = observation?.SourceUpdatedAt ?? package.MarketplaceInvoiceSourceUpdatedAt;
            package.MarketplaceInvoiceObservedAt = observedAt;
            if (discoverySource is not null && !wasInvoiced && incomingStatus == MarketplaceInvoiceStatus.Invoiced)
                await NoteInvoiceDiscoveryAsync(package, discoverySource, observedAt, cancellationToken);
        }
        else if (referenceChanged)
        {
            package.MarketplaceInvoiceSourceUpdatedAt = observation?.SourceUpdatedAt ?? package.MarketplaceInvoiceSourceUpdatedAt;
            package.MarketplaceInvoiceObservedAt = observedAt;
        }
        if (!applyStatus && !referenceChanged) return;

        package.MarketplaceInvoiceObservedAt = observedAt;
        package.UpdatedAt = timeProvider.GetUtcNow();
        package.Version++;
        run.UpdatedCount++;

        // New invoices belong to the order rather than one shipment package.
        // Include those alongside legacy package-scoped invoices, and fail
        // closed if history contains more than one candidate.
        var matchingInvoices = await db.Invoices
            .Where(x => x.TenantId == package.TenantId && x.OrderId == package.OrderId
                && x.OriginalInvoiceId == null && x.SequencePurpose == "SALE"
                && (x.PackageId == null || x.PackageId == package.Id))
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(cancellationToken);
        if (matchingInvoices.Count != 1) return;
        var invoice = matchingInvoices[0];

        if (incomingStatus == MarketplaceInvoiceStatus.NotInvoiced)
        {
            var deliveryState = await db.MarketplaceDeliveryStates.SingleOrDefaultAsync(
                x => x.TenantId == invoice.TenantId && x.InvoiceId == invoice.Id && x.PackageId == package.Id,
                cancellationToken);
            var previouslyConfirmed = string.Equals(deliveryState?.Status, "CONFIRMED", StringComparison.Ordinal);
            var hasDeliveryAttempt = InvoiceDeliveryRecoveryPolicy.HasDeliveryAttempt(deliveryState?.ExternalIdempotencyKey);
            var shouldMarkNotPresent = InvoiceDeliveryRecoveryPolicy.ShouldMarkMarketplaceInvoiceNotPresent(invoice.Status, invoice.LastErrorCode, incomingStatus);
            var shouldQueueRedelivery = InvoiceDeliveryRecoveryPolicy.ShouldQueueAutomaticRedelivery(invoice.Status, invoice.LastErrorCode, incomingStatus, hasDeliveryAttempt);
            if (previouslyConfirmed || shouldMarkNotPresent || shouldQueueRedelivery)
            {
                await RecordMarketplaceInvoiceDeliveryObservationAsync(invoice, package, "REVOKED", observedAt, cancellationToken);
                await UpdateOrderInvoiceDeliveryStatusAsync(invoice, observedAt, cancellationToken,
                    InvoiceDeliveryRecoveryPolicy.MarketplaceInvoiceNotPresentErrorCode);
                await QueueAutomaticInvoiceRedelivery(invoice, package, cancellationToken);
            }
            else if (InvoiceDeliveryRecoveryPolicy.IsNeverDeliveredMarkedNotPresent(invoice.Status, invoice.LastErrorCode, hasDeliveryAttempt,
                await db.InvoiceDocuments.AsNoTracking().AnyAsync(document => document.TenantId == invoice.TenantId && document.InvoiceId == invoice.Id, cancellationToken)))
            {
                // An earlier read marked an invoice nothing was ever sent for as removed: say what happened instead
                // (no document, not delivered). Nothing is queued; the delivery waits for the operator.
                invoice.LastErrorCode = InvoiceDeliveryRecoveryPolicy.DocumentMissingNotDeliveredErrorCode;
                invoice.UpdatedAt = observedAt;
                invoice.Version++;
            }
            return;
        }

        if (incomingStatus == MarketplaceInvoiceStatus.Received
            && observation?.ReferencesAreAuthoritative == true
            && invoice.Status == InvoiceStatus.Completed)
        {
            await RecordMarketplaceInvoiceDeliveryObservationAsync(invoice, package, "REVIEW_REQUIRED", observedAt, cancellationToken);
            await UpdateOrderInvoiceDeliveryStatusAsync(invoice, observedAt, cancellationToken);
            return;
        }

        if (incomingStatus == MarketplaceInvoiceStatus.Invoiced && invoice.Status is InvoiceStatus.Submitted or InvoiceStatus.Accepted or InvoiceStatus.MarketplacePending or InvoiceStatus.Completed)
        {
            var remoteInvoiceNumber = observation?.InvoiceNumber?.Trim();
            var localInvoiceLink = await db.InvoiceDocuments.AsNoTracking()
                .Where(document => document.TenantId == invoice.TenantId && document.InvoiceId == invoice.Id && document.PermanentUrl != null)
                .OrderByDescending(document => document.CreatedAt)
                .Select(document => document.PermanentUrl)
                .FirstOrDefaultAsync(cancellationToken);
            var exactProof = InvoiceDeliveryProofPolicy.HasExactProof(
                new(package.ExternalPackageId, observation?.RawStatus ?? string.Empty, true,
                    observation?.InvoiceNumber, observation?.InvoiceUrl, observation?.ReferencesAreAuthoritative == true),
                invoice.InvoiceNumber,
                localInvoiceLink)
                // A read without references is weaker than what the package already holds: when that is exactly this
                // invoice's number and link, the read confirms it instead of sending a confirmed delivery to review.
                || string.IsNullOrWhiteSpace(observation?.InvoiceNumber) && string.IsNullOrWhiteSpace(observation?.InvoiceUrl)
                    && InvoiceDeliveryRecoveryPolicy.StoredReferencesProveDelivery(
                        package.MarketplaceInvoiceNumber, package.MarketplaceInvoiceUrl, invoice.InvoiceNumber, localInvoiceLink);
            if (exactProof)
            {
                await RecordExactMarketplaceInvoiceProofAsync(invoice, package, observedAt, cancellationToken);
                await UpdateOrderInvoiceDeliveryStatusAsync(invoice, observedAt, cancellationToken);
            }
            else
            {
                var deliveryState = await db.MarketplaceDeliveryStates.SingleOrDefaultAsync(
                    x => x.TenantId == invoice.TenantId && x.InvoiceId == invoice.Id && x.PackageId == package.Id,
                    cancellationToken);
                if (InvoiceDeliveryRecoveryPolicy.ShouldPreservePreviouslyConfirmedDelivery(
                    invoice.Status,
                    deliveryState?.Status,
                    remoteInvoiceNumber))
                    return;

                // The send was preceded by a read that found the package empty, and Hepsiburada has reported an invoice after that write.
                // The platform cannot say which invoice it holds, so the write we just made is the only explanation; later rewrites (REVOKED) reset this.
                var acceptedAttemptAt = deliveryState is null || incomingStatus != MarketplaceInvoiceStatus.Invoiced
                    || deliveryState.Status is not ("UNKNOWN" or "REVIEW_REQUIRED")
                    ? (DateTimeOffset?)null
                    : await db.MarketplaceDeliveries.AsNoTracking()
                        .Where(x => x.TenantId == invoice.TenantId && x.InvoiceId == invoice.Id && x.PackageId == package.Id
                            && x.ErrorCode == InvoiceDeliveryRecoveryPolicy.HepsiburadaAcceptedButReadbackMissingErrorCode)
                        .OrderByDescending(x => x.CreatedAt).Select(x => (DateTimeOffset?)x.CreatedAt).FirstOrDefaultAsync(cancellationToken);
                var revokedAfterAttempt = acceptedAttemptAt is { } attemptAt && await db.MarketplaceDeliveries.AsNoTracking()
                    .AnyAsync(x => x.TenantId == invoice.TenantId && x.InvoiceId == invoice.Id && x.PackageId == package.Id && x.Status == "REVOKED" && x.CreatedAt > attemptAt, cancellationToken);
                var sameAttemptProof = deliveryState is not null && acceptedAttemptAt is { } acceptedAt && !revokedAfterAttempt
                    && observedAt is { } readAt && readAt > acceptedAt;

                if (sameAttemptProof && deliveryState is not null)
                {
                    RecordAcceptedHepsiburadaReadbackProof(deliveryState, package, observedAt);
                    await UpdateOrderInvoiceDeliveryStatusAsync(invoice, observedAt, cancellationToken);
                }
                else
                {
                    await RecordMarketplaceInvoiceDeliveryObservationAsync(invoice, package, "REVIEW_REQUIRED", observedAt, cancellationToken);
                    SetInvoiceDeliveryState(invoice, InvoiceStatus.MarketplacePending,
                        InvoiceDeliveryRecoveryPolicy.MarketplaceInvoiceIdentityMismatchErrorCode, observedAt);
                }
            }
        }
        else if (incomingStatus == MarketplaceInvoiceStatus.Rejected && invoice.Status is InvoiceStatus.Submitted or InvoiceStatus.Accepted or InvoiceStatus.MarketplacePending)
        {
            var delivery = await db.MarketplaceDeliveryStates.AsNoTracking()
                .SingleOrDefaultAsync(x => x.TenantId == package.TenantId && x.InvoiceId == invoice.Id && x.PackageId == package.Id, cancellationToken);
            var rejectedAfterDeliveryAttempt = delivery is not null
                && observation?.SourceUpdatedAt is { } remoteUpdatedAt
                && remoteUpdatedAt > delivery.CreatedAt;
            if (rejectedAfterDeliveryAttempt)
            {
                invoice.Status = InvoiceStatus.MarketplaceFailed;
                invoice.LastErrorCode = "REMOTE_INVOICE_REJECTED";
                invoice.UpdatedAt = observedAt;
                invoice.Version++;
            }
        }
    }

    private async Task QueueAutomaticInvoiceRedelivery(Invoice invoice, ShipmentPackage package, CancellationToken cancellationToken)
    {
        if (invoiceBilling is null) return;
        // An invoice in review is the owner's, and a delivery job of this package that waits, runs or stopped for review
        // already stands for it: no second job next to it under another time bucket (live 13 Oct 2026).
        if (invoice.Status == InvoiceStatus.ManualReview
            || await InvoiceDeliveryJobHygiene.AutomaticDeliveryHeldAsync(db, invoice.TenantId, invoice.Id, package.Id, reviewHolds: true, cancellationToken)) return;
        var state = db.ChangeTracker.Entries<MarketplaceDeliveryState>()
            .Where(entry => entry.Entity.TenantId == invoice.TenantId
                && entry.Entity.InvoiceId == invoice.Id
                && entry.Entity.PackageId == package.Id)
            .Select(entry => entry.Entity)
            .SingleOrDefault();
        var stateVersion = state?.Version ?? await db.MarketplaceDeliveryStates.AsNoTracking()
            .Where(item => item.TenantId == invoice.TenantId && item.InvoiceId == invoice.Id && item.PackageId == package.Id)
            .Select(item => (long?)item.Version)
            .SingleOrDefaultAsync(cancellationToken);
        if (stateVersion is null) return;

        var retryBucket = timeProvider.GetUtcNow().ToUnixTimeSeconds() / 900;
        var idempotencyKey = $"invoice-reconcile-redelivery:{invoice.Id:N}:{package.Id:N}:{stateVersion.Value}:{retryBucket}";
        await invoiceBilling.EnqueueDeliveryAsync(invoice.TenantId, invoice.Id, idempotencyKey,
            $"invoice-reconcile:{invoice.Id:N}:{package.Id:N}", cancellationToken);
    }

    private async Task RecordMarketplaceInvoiceDeliveryObservationAsync(
        Invoice invoice,
        ShipmentPackage package,
        string status,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        var state = await db.MarketplaceDeliveryStates.SingleOrDefaultAsync(
            x => x.TenantId == invoice.TenantId && x.InvoiceId == invoice.Id && x.PackageId == package.Id,
            cancellationToken);
        if (state?.Status == status) return;

        if (state is null)
        {
            var latestAttemptNumber = await db.MarketplaceDeliveries.AsNoTracking()
                .Where(x => x.TenantId == invoice.TenantId && x.InvoiceId == invoice.Id && x.PackageId == package.Id)
                .Select(x => (int?)x.AttemptNumber)
                .MaxAsync(cancellationToken) ?? 0;
            var proofKey = $"marketplace-observation:{invoice.Id:N}:{package.Id:N}";
            var proofHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{invoice.Id:N}|{package.Id:N}|{status}|{package.MarketplaceInvoiceNumber}|{package.MarketplaceInvoiceUrl}")));
            state = new MarketplaceDeliveryState
            {
                Id = Guid.CreateVersion7(),
                TenantId = invoice.TenantId,
                InvoiceId = invoice.Id,
                ConnectionId = package.ConnectionId,
                PackageId = package.Id,
                AttemptNumber = latestAttemptNumber,
                ExternalIdempotencyKey = proofKey,
                RequestHash = proofHash,
                DeliveryType = "LINK",
                Status = "UNKNOWN",
                ExternalReference = package.ExternalPackageId,
                CreatedAt = observedAt,
                UpdatedAt = observedAt,
                Version = 1
            };
            db.MarketplaceDeliveryStates.Add(state);
        }

        var sequence = state.AttemptNumber + 1;
        db.MarketplaceDeliveries.Add(new MarketplaceDelivery
        {
            Id = Guid.CreateVersion7(),
            TenantId = state.TenantId,
            InvoiceId = state.InvoiceId,
            ConnectionId = state.ConnectionId,
            PackageId = state.PackageId,
            AttemptNumber = sequence,
            IdempotencyKey = $"delivery-observation:{state.Id:N}:{sequence}",
            ExternalIdempotencyKey = state.ExternalIdempotencyKey,
            RequestHash = state.RequestHash,
            DeliveryType = state.DeliveryType,
            Status = status,
            ExternalReference = package.ExternalPackageId,
            ErrorCode = status == "REVOKED" ? InvoiceDeliveryRecoveryPolicy.MarketplaceInvoiceNotPresentErrorCode : InvoiceDeliveryRecoveryPolicy.MarketplaceInvoiceIdentityMismatchErrorCode,
            CreatedAt = observedAt,
            CompletedAt = observedAt
        });
        state.AttemptNumber = sequence;
        state.Status = status;
        state.ExternalReference = package.ExternalPackageId;
        state.ErrorCode = status == "REVOKED" ? InvoiceDeliveryRecoveryPolicy.MarketplaceInvoiceNotPresentErrorCode : InvoiceDeliveryRecoveryPolicy.MarketplaceInvoiceIdentityMismatchErrorCode;
        state.UpdatedAt = observedAt;
        state.CompletedAt = observedAt;
        state.Version++;
    }

    private async Task RecordExactMarketplaceInvoiceProofAsync(Invoice invoice, ShipmentPackage package, DateTimeOffset observedAt, CancellationToken cancellationToken)
    {
        var state = await db.MarketplaceDeliveryStates.SingleOrDefaultAsync(
            x => x.TenantId == invoice.TenantId && x.InvoiceId == invoice.Id && x.PackageId == package.Id,
            cancellationToken);
        var proofKey = $"remote-invoice-proof:{invoice.Id:N}:{package.Id:N}";
        var proofHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{invoice.Id:N}|{package.Id:N}|{invoice.InvoiceNumber?.Trim()}|{package.MarketplaceInvoiceNumber?.Trim()}|{package.MarketplaceInvoiceUrl?.Trim()}")));
        if (state is null)
        {
            state = new MarketplaceDeliveryState
            {
                Id = Guid.CreateVersion7(),
                TenantId = invoice.TenantId,
                InvoiceId = invoice.Id,
                ConnectionId = package.ConnectionId,
                PackageId = package.Id,
                AttemptNumber = 0,
                ExternalIdempotencyKey = proofKey,
                RequestHash = proofHash,
                DeliveryType = "LINK",
                Status = "CONFIRMED",
                ExternalReference = package.ExternalPackageId,
                CreatedAt = observedAt,
                UpdatedAt = observedAt,
                CompletedAt = observedAt,
                Version = 1
            };
            db.MarketplaceDeliveryStates.Add(state);
            return;
        }
        // The same proof read again changes nothing.
        if (state.Status == "CONFIRMED" && state.ErrorCode is null && state.ExternalIdempotencyKey == proofKey && state.RequestHash == proofHash
            && state.ConnectionId == package.ConnectionId && state.ExternalReference == package.ExternalPackageId)
            return;

        state.ConnectionId = package.ConnectionId;
        state.PackageId = package.Id;
        state.ExternalIdempotencyKey = proofKey;
        state.RequestHash = proofHash;
        state.DeliveryType = "LINK";
        state.Status = "CONFIRMED";
        state.ExternalReference = package.ExternalPackageId;
        state.ErrorCode = null;
        state.UpdatedAt = observedAt;
        state.CompletedAt = observedAt;
        state.Version++;
    }

    private async Task UpdateOrderInvoiceDeliveryStatusAsync(
        Invoice invoice,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken,
        string? incompleteErrorCode = null)
    {
        var targetPackageIds = invoice.PackageId is { } legacyPackageId
            ? new[] { legacyPackageId }
            : await db.ShipmentPackages.AsNoTracking()
                .Where(x => x.TenantId == invoice.TenantId && x.OrderId == invoice.OrderId && x.Status != ShipmentPackageStatus.Cancelled)
                .Select(x => x.Id)
                .ToArrayAsync(cancellationToken);
        var states = await db.MarketplaceDeliveryStates
            .Where(x => x.TenantId == invoice.TenantId && x.InvoiceId == invoice.Id
                && x.PackageId != null && targetPackageIds.Contains(x.PackageId.Value))
            .ToListAsync(cancellationToken);
        var trackedStates = db.ChangeTracker.Entries<MarketplaceDeliveryState>()
            .Where(entry => (entry.State is EntityState.Added or EntityState.Modified)
                && entry.Entity.TenantId == invoice.TenantId && entry.Entity.InvoiceId == invoice.Id
                && entry.Entity.PackageId.HasValue && targetPackageIds.Contains(entry.Entity.PackageId.Value))
            .Select(entry => entry.Entity);
        var confirmedPackageIds = states.Concat(trackedStates)
            .DistinctBy(x => x.Id)
            .Where(x => string.Equals(x.Status, "CONFIRMED", StringComparison.Ordinal) && x.PackageId.HasValue)
            .Select(x => x.PackageId!.Value)
            .ToHashSet();
        var allConfirmed = MarketplaceInvoiceDeliveryEvidencePolicy.AllTargetsConfirmed(targetPackageIds, confirmedPackageIds);
        SetInvoiceDeliveryState(invoice,
            allConfirmed ? InvoiceStatus.Completed
                : incompleteErrorCode is not null ? InvoiceStatus.MarketplaceFailed
                : InvoiceStatus.MarketplacePending,
            allConfirmed ? null : incompleteErrorCode,
            observedAt);
    }

    // A read that finds the invoice as it already is writes nothing: Version and UpdatedAt move only on a real change,
    // so an unchanged invoice is neither "updated" on every scan nor raced by the optimistic lock.
    // An invoice in ManualReview is the owner's: a marketplace read never moves it out nor rewrites its reason
    // (InvoiceDeliveryGuardPolicy.BackgroundMayMove).
    internal static bool SetInvoiceDeliveryState(Invoice invoice, InvoiceStatus status, string? lastErrorCode, DateTimeOffset at)
    {
        if (invoice.Status == InvoiceStatus.ManualReview) return false;
        if (!InvoiceDeliveryRecoveryPolicy.DeliveryStateChanges(invoice.Status, invoice.LastErrorCode, status, lastErrorCode)) return false;
        invoice.Status = status;
        invoice.LastErrorCode = lastErrorCode;
        invoice.UpdatedAt = at;
        invoice.Version++;
        return true;
    }

    private void RecordAcceptedHepsiburadaReadbackProof(MarketplaceDeliveryState state, ShipmentPackage package, DateTimeOffset observedAt)
    {
        var sequence = state.AttemptNumber + 1;
        db.MarketplaceDeliveries.Add(new MarketplaceDelivery
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
            Status = "CONFIRMED",
            ExternalReference = package.ExternalPackageId,
            CreatedAt = observedAt,
            CompletedAt = observedAt
        });
        state.AttemptNumber = sequence;
        state.Status = "CONFIRMED";
        state.ExternalReference = package.ExternalPackageId;
        state.ErrorCode = null;
        state.UpdatedAt = observedAt;
        state.CompletedAt = observedAt;
        state.Version++;
    }
}
