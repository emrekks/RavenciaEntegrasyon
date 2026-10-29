using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// An instant action whose marketplace response was unclear (timeout, 5xx) leaves an open effect
// record. This job reads the marketplace instead of sending again: if the action took effect the
// record is completed; if it clearly did not, the record is released so the user can try again;
// if the marketplace cannot tell after several reads over 30 minutes of verification (or a day after the action,
// when the reads came too far apart to count), it is closed as unresolved and reported.
public sealed partial class MarketplaceJobProcessor
{
    private async Task<JobExecutionResult> ReconcileExternalEffect(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        Guid effectId;
        try
        {
            using var payload = JsonDocument.Parse(payloadJson);
            effectId = payload.RootElement.GetProperty("effectId").GetGuid();
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
        {
            return JobExecutionResult.Blocked("EFFECT_RECONCILE_PAYLOAD_INVALID", "Doğrulanacak işlem kaydı belirtilmemiş.");
        }
        var effect = await db.ExternalEffectRecords.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == effectId, cancellationToken);
        if (effect is null || effect.CompletedAt is not null) return JobExecutionResult.Success();

        var now = timeProvider.GetUtcNow();
        var verdict = effect.Action switch
        {
            "PICKING" or "CHANGE_CARGO_PROVIDER" => await PackageEffectVerdictAsync(tenantId, connectionId, effect, correlationId, cancellationToken),
            "CREATE_PACKAGE" => await PackageCreationVerdictAsync(tenantId, connectionId, effect, correlationId, cancellationToken),
            "RETURN_APPROVE" or "RETURN_REJECT" => await ReturnEffectVerdictAsync(tenantId, connectionId, effect, correlationId, cancellationToken),
            _ => EffectVerdict.Unknown
        };
        // Every return action (approve, reject, Hepsiburada's send-for-review)
        // has a decision row that must leave its in-progress state with the effect.
        var decision = effect.Action?.StartsWith("RETURN_", StringComparison.Ordinal) == true
            ? await db.ReturnDecisions.Where(x => x.TenantId == tenantId && x.IdempotencyKey == effect.IdempotencyKey).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(cancellationToken)
            : null;
        // Unclear reads are counted with the verification time actually spent (a gap while the worker was
        // stopped does not count), so a single unclear read after a restart cannot close an older action.
        if (verdict == EffectVerdict.Unknown)
            (effect.UnclearChecks, effect.VerificationSeconds) = ExternalEffectReconcilePolicy.RecordUnclearCheck(effect.UnclearChecks, effect.VerificationSeconds, effect.LastCheckedAt, now);
        effect.LastCheckedAt = now;
        var outcome = ExternalEffectReconcilePolicy.Decide(verdict, now - effect.CreatedAt, effect.UnclearChecks, TimeSpan.FromSeconds(effect.VerificationSeconds));
        switch (outcome)
        {
            case ExternalEffectOutcome.Applied:
                effect.CompletedAt = now;
                effect.Outcome = ExternalEffectReconcilePolicy.AppliedOutcome;
                if (decision is not null) { decision.Status = "SUBMITTED"; decision.ErrorCode = null; }
                await ResolveIssue(tenantId, $"external-effect:{effect.Id}", cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                return JobExecutionResult.Success();
            case ExternalEffectOutcome.NotApplied:
                db.ExternalEffectRecords.Remove(effect);
                if (decision is not null) { decision.Status = "FAILED"; decision.ErrorCode = "EXTERNAL_EFFECT_NOT_APPLIED"; decision.CompletedAt = now; }
                await RecordIssue(tenantId, $"external-effect:{effect.Id}", "EXTERNAL_EFFECT_NOT_APPLIED",
                    "Pazaryerine gönderilen işlem uygulanmamış görünüyor; panelden yeniden deneyebilirsiniz.", cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                return JobExecutionResult.Success();
            case ExternalEffectOutcome.Unresolved:
                effect.CompletedAt = now;
                effect.Outcome = ExternalEffectReconcilePolicy.UnresolvedOutcome;
                // The decision must not stay "in progress" forever: it fails
                // with a code the next claim read can still upgrade to success.
                if (decision is not null) { decision.Status = "FAILED"; decision.ErrorCode = "EXTERNAL_EFFECT_UNRESOLVED"; decision.CompletedAt = now; }
                await RecordIssue(tenantId, $"external-effect:{effect.Id}", "EXTERNAL_EFFECT_UNRESOLVED",
                    "Pazaryerine gönderilen işlemin sonucu tekrarlanan doğrulamada kesinleşmedi; pazaryeri panelinden kontrol edin.", cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                return JobExecutionResult.Success();
            default:
                // The read count and verification time are kept for the next read.
                await db.SaveChangesAsync(cancellationToken);
                return JobExecutionResult.Continue("İşlemin sonucu pazaryerinden kısa süre içinde yeniden okunacak.", TimeSpan.FromSeconds(30));
        }
    }

    // Picking moves the package out of New; a cargo provider change leaves the new provider on it.
    private async Task<EffectVerdict> PackageEffectVerdictAsync(Guid tenantId, Guid connectionId, ExternalEffectRecord effect, string correlationId, CancellationToken cancellationToken)
    {
        var target = await db.ShipmentPackages.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Id == effect.TargetId)
            .Select(x => new { x.OrderId })
            .SingleOrDefaultAsync(cancellationToken);
        if (target is null) return EffectVerdict.Unknown;
        if (!await RefreshOrderAsync(tenantId, connectionId, target.OrderId, $"{effect.IdempotencyKey}:verify", correlationId, cancellationToken)) return EffectVerdict.Unknown;
        var package = await db.ShipmentPackages.AsNoTracking().SingleAsync(x => x.TenantId == tenantId && x.Id == effect.TargetId, cancellationToken);
        var applied = effect.Action == "PICKING"
            ? package.Status is not (ShipmentPackageStatus.New or ShipmentPackageStatus.PaymentPending)
            : string.Equals(package.CargoProviderExternalId, effect.ExpectedValue, StringComparison.OrdinalIgnoreCase);
        return applied ? EffectVerdict.Applied : EffectVerdict.NotApplied;
    }

    // A package creation shows as more packages on the order than before the request.
    private async Task<EffectVerdict> PackageCreationVerdictAsync(Guid tenantId, Guid connectionId, ExternalEffectRecord effect, string correlationId, CancellationToken cancellationToken)
    {
        if (effect.TargetId is not { } orderId || !int.TryParse(effect.ExpectedValue, out var packagesBefore)) return EffectVerdict.Unknown;
        if (!await RefreshOrderAsync(tenantId, connectionId, orderId, $"{effect.IdempotencyKey}:verify", correlationId, cancellationToken)) return EffectVerdict.Unknown;
        var packages = await db.ShipmentPackages.AsNoTracking().CountAsync(x => x.TenantId == tenantId && x.OrderId == orderId && x.Status != ShipmentPackageStatus.Cancelled, cancellationToken);
        return packages > packagesBefore ? EffectVerdict.Applied : EffectVerdict.NotApplied;
    }

    // A return decision takes the claim out of the state that waited for the seller's action.
    private async Task<EffectVerdict> ReturnEffectVerdictAsync(Guid tenantId, Guid connectionId, ExternalEffectRecord effect, string correlationId, CancellationToken cancellationToken)
    {
        var claim = await db.ReturnClaims.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Id == effect.TargetId)
            .Select(x => new { x.Id, x.ExternalClaimId })
            .SingleOrDefaultAsync(cancellationToken);
        if (claim is null) return EffectVerdict.Unknown;
        TrackRequest();
        var remote = await returns.GetAsync(Context(tenantId, connectionId, correlationId, $"{effect.IdempotencyKey}:verify"), claim.ExternalClaimId, cancellationToken);
        if (!remote.IsSuccess) { TrackResultFailure(remote.Error); return EffectVerdict.Unknown; }
        TrackReceived();
        await UpsertReturn(tenantId, connectionId, correlationId, remote.Value!, new Dictionary<string, string?>(StringComparer.Ordinal), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        // A decision on some of the claim's lines is judged by those lines: the claim stays actionable while its other
        // lines wait for their own decision.
        var decisionLineIds = await db.ReturnDecisions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.IdempotencyKey == effect.IdempotencyKey)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => x.ReturnLineIds)
            .FirstOrDefaultAsync(cancellationToken);
        if (decisionLineIds is { Length: > 0 })
        {
            var lineStatuses = await db.ReturnLines.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ClaimId == claim.Id && decisionLineIds.Contains(x.Id))
                .Select(x => x.RawStatus)
                .ToListAsync(cancellationToken);
            if (lineStatuses.Count == decisionLineIds.Length && lineStatuses.All(status => !string.IsNullOrWhiteSpace(status)))
                return lineStatuses.Any(ReturnLineStatusPolicy.IsActionable) ? EffectVerdict.NotApplied : EffectVerdict.Applied;
        }
        var status = await db.ReturnClaims.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == claim.Id).Select(x => x.Status).SingleAsync(cancellationToken);
        return status is ReturnClaimStatus.ActionRequired or ReturnClaimStatus.Requested ? EffectVerdict.NotApplied : EffectVerdict.Applied;
    }

    private async Task<bool> RefreshOrderAsync(Guid tenantId, Guid connectionId, Guid orderId, string purpose, string correlationId, CancellationToken cancellationToken)
    {
        var order = await db.Orders.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Id == orderId)
            .Select(x => new { x.ExternalOrderId, Hint = new OrderLookupHint(x.OrderedAt, x.LastRemoteModifiedAt, x.StoreFrontCode) })
            .SingleOrDefaultAsync(cancellationToken);
        if (order is null) return false;
        TrackRequest();
        var remote = await orders.GetAsync(Context(tenantId, connectionId, correlationId, purpose), order.ExternalOrderId, order.Hint, cancellationToken);
        if (!remote.IsSuccess) { TrackResultFailure(remote.Error); return false; }
        TrackReceived();
        await UpsertOrder(tenantId, connectionId, remote.Value!, cancellationToken);
        return true;
    }
}
