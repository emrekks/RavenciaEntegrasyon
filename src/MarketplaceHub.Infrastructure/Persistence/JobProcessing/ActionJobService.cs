using System.Security.Cryptography;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// ISLEYICI-9 stage 5 step 3a (16 Nov 2026): the package actions (SHIPMENT_ACTION) and the return decisions (RETURN_ACTION),
// moved out of the job processor as they were. Each send opens, sends and closes its ExternalEffect record through the
// processor's ExternalEffectGuard; an unclear answer is never sent again on its own, and a record closed as unresolved
// is judged by a read only (ExternalEffectReconcileService). It works for one run of one job (run) on the job's AppDbContext.
internal sealed class ActionJobService(
    AppDbContext db,
    IssueRecorder issues,
    IOrderPort orders,
    IReturnPort returns,
    IPrivateFileStorage files,
    TimeProvider timeProvider,
    ExternalEffectGuard effectGuard,
    JobWriteSwitches writeSwitches,
    OrderIngestionService orderIngestion,
    ReturnIngestionService returnIngestion,
    ExternalEffectReconcileService effectReconcile,
    JobRunContext run)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // What the moved code used from the processor: the same classes and helpers the processor calls.
    private Task RecordIssue(Guid tenantId, string key, string code, string summary, CancellationToken cancellationToken) =>
        issues.RecordAsync(tenantId, key, code, summary, timeProvider.GetUtcNow(), cancellationToken);

    private Task ResolveIssue(Guid tenantId, string key, CancellationToken cancellationToken) =>
        issues.ResolveAsync(tenantId, key, timeProvider.GetUtcNow(), cancellationToken);

    private AdapterContext Context(Guid tenantId, Guid connectionId, string correlationId, string idempotency) => new(tenantId, connectionId, correlationId, idempotency, timeProvider.GetUtcNow().AddMinutes(2));

    private void TrackRequest() => run.TrackRequest();

    private void TrackReceived() => run.TrackReceived();

    private void TrackResultFailure(AdapterError? error) => run.TrackResultFailure(error);

    private Task<bool> ExternalWriteAllowedAsync(Guid tenantId, Guid connectionId, string resourceType, CancellationToken cancellationToken) =>
        writeSwitches.ExternalWriteAllowedAsync(tenantId, connectionId, resourceType, cancellationToken);

    private Task<string?> ConnectionPlatformCode(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        orderIngestion.ConnectionPlatformCode(tenantId, connectionId, cancellationToken);

    private Task<bool> UpsertOrder(Guid tenantId, Guid connectionId, RemoteOrder remote, CancellationToken cancellationToken) =>
        orderIngestion.UpsertOrder(tenantId, connectionId, remote, cancellationToken);

    private Task UpsertReturn(Guid tenantId, Guid connectionId, RemoteReturnClaim remote, CancellationToken cancellationToken) =>
        returnIngestion.UpsertReturn(tenantId, connectionId, remote, cancellationToken);

    private Task<EffectVerdict> PackageEffectVerdictAsync(Guid tenantId, Guid connectionId, ExternalEffectRecord effect, string correlationId, CancellationToken cancellationToken) =>
        effectReconcile.PackageEffectVerdictAsync(tenantId, connectionId, effect, correlationId, cancellationToken);

    internal static string? SafeReturnTrackingLink(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri.ToString()
            : null;

    internal async Task<bool> ShipmentAction(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        ShipmentActionJobPayload? payload;
        try { payload = JsonSerializer.Deserialize<ShipmentActionJobPayload>(payloadJson, JsonOptions); }
        // J1 (15 Nov 2026): each refusal before the send names its cause instead of the generic F3_JOB_REJECTED. All of them
        // are lasting: the request is the panel's own, and a package row is only removed by a reset (a split keeps it), so a
        // later run would find the same thing; nothing was sent to the marketplace.
        catch (JsonException) { throw ShipmentActionPayloadInvalid(); }
        if (payload is null || payload.JobId == Guid.Empty || payload.PackageId == Guid.Empty || string.IsNullOrWhiteSpace(payload.Action)) throw ShipmentActionPayloadInvalid();
        var job = await db.IntegrationJobs.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == payload.JobId && x.ConnectionId == connectionId && x.JobType == MarketplaceJobTypes.ShipmentAction, cancellationToken);
        if (job is null) throw ShipmentActionPayloadInvalid();
        var package = await db.ShipmentPackages.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == payload.PackageId && x.ConnectionId == connectionId, cancellationToken);
        if (package is null) throw new JobProcessingException(JobExecutionResult.Blocked("SHIPMENT_PACKAGE_NOT_FOUND", "Paket panelde artık yok (mağaza verisi sıfırlanmış olabilir); pazaryerine bir şey gönderilmedi. Siparişi yenileyip işlemi yeniden başlatın."));
        if (!await ExternalWriteAllowedAsync(tenantId, connectionId, MarketplaceExternalWritePolicies.Shipment, cancellationToken)) throw new JobProcessingException(JobExecutionResult.Blocked("EXTERNAL_WRITE_POLICY_DISABLED", "Kargo dış yazma akışı kapalı; pazar yeri isteği gönderilmedi."));
        var effect = await effectGuard.FindAsync(tenantId, MarketplaceJobTypes.ShipmentAction, job.EffectIdempotencyKey, cancellationToken);
        switch (ExternalEffectGuard.Gate(effect))
        {
            case ExternalEffectGate.Open: throw new JobProcessingException(JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS", "Önceki paket aksiyonunun sonucu kesinleştirilemedi; tekrar gönderim engellendi."));
            case ExternalEffectGate.Unresolved: return await VerifyUnresolvedShipmentEffect(tenantId, connectionId, effect!, correlationId, cancellationToken);
            case ExternalEffectGate.Done: return true;
        }
        // The record carries what the action changes, like the instant actions do, so an unclear outcome is
        // verified by reading the marketplace instead of waiting a week to be closed as unresolved.
        var (effectAction, expectedValue) = ShipmentEffectContext(payload.Action, payload.PayloadJson);
        // ISLEYICI-6 (15 Nov 2026): every database read comes before the effect record; after it only the marketplace
        // call runs, so a failed read can no longer leave a request that was never sent as an unclear effect.
        var storeFrontCode = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == package.OrderId).Select(x => x.StoreFrontCode).SingleOrDefaultAsync(cancellationToken);
        effect = effectGuard.New(tenantId, MarketplaceJobTypes.ShipmentAction, job.EffectIdempotencyKey, connectionId, package.Id, effectAction, expectedValue);
        var send = await effectGuard.SendAsync(effect, () =>
        {
            TrackRequest();
            return orders.ExecutePackageActionAsync(Context(tenantId, connectionId, correlationId, job.EffectIdempotencyKey), new(package.ExternalPackageId, payload.Action, payload.PayloadJson, storeFrontCode), cancellationToken);
        }, cancellationToken);
        if (!send.Accepted)
        {
            TrackResultFailure(send.Error);
            if (send.Outcome == ExternalEffectSendOutcome.Uncertain) throw new JobProcessingException(JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS", "Paket aksiyonunun uzak tarafta uygulanıp uygulanmadığı kesinleştirilemedi.", send.Error.RemoteRequestId));
            await effectGuard.ReleaseAsync(effect, cancellationToken); throw JobProcessingException.FromAdapter(send.Error);
        }
        // KUYRUK-2 step 4: a record an effect check removed meanwhile is put back as applied instead of failing the save.
        await effectGuard.SaveAcceptedAsync([effect], cancellationToken);
        if (string.Equals(payload.Action, "UNPACK", StringComparison.OrdinalIgnoreCase))
            await MarkHepsiburadaPackageUnpackedAsync(tenantId, connectionId, package.Id, effect.CompletedAt!.Value, cancellationToken);
        await RecordN11ShipmentWriteEvidenceAsync(tenantId, connectionId, payload.Action, cancellationToken);
        var readbackOrder = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == package.OrderId).Select(x => new { x.OrderNumber, Hint = new OrderLookupHint(x.OrderedAt, x.LastRemoteModifiedAt, x.StoreFrontCode) }).SingleAsync(cancellationToken);
        TrackRequest();
        var readback = await orders.GetAsync(Context(tenantId, connectionId, correlationId, $"{job.EffectIdempotencyKey}:readback"), readbackOrder.OrderNumber, readbackOrder.Hint, cancellationToken);
        if (readback.IsSuccess) TrackReceived();
        if (readback.IsSuccess) await UpsertOrder(tenantId, connectionId, readback.Value!, cancellationToken);
        else { await RecordIssue(tenantId, $"shipment-readback:{connectionId}:{package.ExternalPackageId}:{payload.Action}", "SHIPMENT_ACTION_READBACK_PENDING", "Paket aksiyonu kabul edildi ancak anlık order read-back tamamlanamadı; planlı sync kesinleştirecek.", cancellationToken); await db.SaveChangesAsync(cancellationToken); }
        return true;
    }

    private static JobProcessingException ShipmentActionPayloadInvalid() =>
        new(JobExecutionResult.Blocked("SHIPMENT_ACTION_PAYLOAD_INVALID", "Paket işlemi isteği okunamadı; pazaryerine bir şey gönderilmedi. İşlemi sipariş ekranından yeniden başlatın."));

    // Q-2: a record closed as unresolved was sent once with an outcome no read could confirm. A retry must not
    // send it again (a second split or picking) nor take it as done: where an order read can judge the action
    // (picking, a carrier change) it reads once and succeeds only when the read shows the action applied;
    // everything else stays in manual review for a look at the marketplace panel.
    private async Task<bool> VerifyUnresolvedShipmentEffect(Guid tenantId, Guid connectionId, ExternalEffectRecord effect, string correlationId, CancellationToken cancellationToken)
    {
        if (effect.Action is "PICKING" or "CHANGE_CARGO_PROVIDER" && await PackageEffectVerdictAsync(tenantId, connectionId, effect, correlationId, cancellationToken) == EffectVerdict.Applied)
        {
            effect.Outcome = ExternalEffectReconcilePolicy.AppliedOutcome;
            await ResolveIssue(tenantId, $"external-effect:{effect.Id}", cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        throw new JobProcessingException(JobExecutionResult.ManualReview("EXTERNAL_EFFECT_UNRESOLVED", "Önceki paket aksiyonunun sonucu doğrulanamadan kapatıldı; tekrar gönderilmedi. Pazaryeri panelinden kontrol edin."));
    }

    // The action an open shipment effect is verified by, only where the verification job can judge it: picking
    // (the package leaves New) and a Trendyol carrier change (the package shows the requested carrier). Other
    // actions, and a Hepsiburada carrier change whose short name is not the carrier the order read shows, carry
    // no action: a read could not tell, so they keep the old close-after-a-week path.
    internal static (string? Action, string? ExpectedValue) ShipmentEffectContext(string action, string? payloadJson)
    {
        var normalized = action.Trim().ToUpperInvariant();
        if (normalized == "PICKING") return (normalized, null);
        if (normalized != "CHANGE_CARGO_PROVIDER" || string.IsNullOrWhiteSpace(payloadJson)) return (null, null);
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var provider = document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("cargoProvider", out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()?.Trim()
                    : null;
            return string.IsNullOrEmpty(provider) || provider.Length > 256 ? (null, null) : (normalized, provider);
        }
        catch (JsonException) { return (null, null); }
    }

    // Hepsiburada dissolves an unpacked package: its lines return to the order and later reads no
    // longer list it. The local row stays as superseded history (no tab, count, amount or line
    // allocation), so the order is not shown with a package that no longer exists.
    internal async Task MarkHepsiburadaPackageUnpackedAsync(Guid tenantId, Guid connectionId, Guid packageId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var platform = await ConnectionPlatformCode(tenantId, connectionId, cancellationToken);
        if (platform != "HEPSIBURADA") return;
        var package = await db.ShipmentPackages.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == packageId, cancellationToken);
        if (package is null || ShipmentPackageSupersession.IsLocallyUnpacked(package.RawStatus)) return;
        package.Status = ShipmentPackageStatus.Cancelled;
        package.RawStatus = ShipmentPackageSupersession.LocallyUnpackedRawStatus;
        package.UpdatedAt = now;
        package.Version++;
        db.OrderStatusHistory.Add(new OrderStatusHistory { Id = Guid.CreateVersion7(), TenantId = tenantId, OrderId = package.OrderId, PackageId = package.Id, CanonicalStatus = "CANCELLED", RawStatus = ShipmentPackageSupersession.LocallyUnpackedRawStatus, SourceEventId = $"panel-unpack:{package.Id:N}:{now.ToUnixTimeMilliseconds()}", OccurredAt = now, RecordedAt = now });
        await db.SaveChangesAsync(cancellationToken);
    }

    // n11 has no sandbox and its probe is read-only, so SHIPMENT_WRITE is proven by the first
    // package action the live store accepts. Once recorded, the shipment detail offers the
    // n11 package actions (approve and splits) like the other marketplaces.
    internal static readonly string[] N11ShipmentActions = ["PICKING", "SPLIT", "MULTI_SPLIT", "SPLIT_BY_QUANTITY", "LABOR_COST", "COLLECTION_REQUEST"];

    private async Task RecordN11ShipmentWriteEvidenceAsync(Guid tenantId, Guid connectionId, string action, CancellationToken cancellationToken)
    {
        if (!N11ShipmentActions.Contains(action, StringComparer.OrdinalIgnoreCase)) return;
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && x.PlatformCode == "N11", cancellationToken);
        if (connection is null) return;
        var capability = await db.PlatformCapabilities.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Code == MarketplaceCapabilities.ShipmentWrite, cancellationToken);
        if (capability is null || capability.SupportLevel == CapabilitySupportLevel.Supported && capability.VerifiedAt is not null) return;
        capability.SupportLevel = CapabilitySupportLevel.Supported;
        capability.SourceUrl = "https://api.n11.com/rest/order/v1/update";
        capability.SourceVersion = "V1";
        capability.Environment = connection.Environment;
        capability.StoreScope = connection.ExternalStoreId;
        capability.ConstraintsJson = JsonSerializer.Serialize(new { allowedActions = N11ShipmentActions });
        capability.EvidenceNote = $"SHIPMENT_WRITE, N11 canlı mağazasının kabul ettiği ilk paket aksiyonuyla ({action.ToUpperInvariant()}) doğrulandı.";
        capability.VerifiedAt = timeProvider.GetUtcNow();
        capability.Version++;
        await db.SaveChangesAsync(cancellationToken);
    }

    internal async Task<bool> ReturnAction(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        // Only a payload that cannot be read blocks the job for good, and a missing decision or claim is checked
        // below. Any other failure (a dropped connection, a database fault) reaches the worker's retry handling
        // instead of being taken for a bad payload and blocking the return decision.
        Guid decisionId;
        Guid[]? selectedReturnLineIds;
        try
        {
            using var payload = JsonDocument.Parse(payloadJson);
            decisionId = payload.RootElement.GetProperty("decisionId").GetGuid();
            selectedReturnLineIds = payload.RootElement.TryGetProperty("returnLineIds", out var selectedLinesElement) && selectedLinesElement.ValueKind == JsonValueKind.Array
                ? selectedLinesElement.EnumerateArray().Select(value => value.GetGuid()).Distinct().ToArray()
                : null;
        }
        // J1 (15 Nov 2026): every stop before the send names its cause; none of them changes on a later run (a decision or
        // claim is only removed with a cancelled claim or a reset), so they stay blocked, and nothing was sent.
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
        {
            throw new JobProcessingException(JobExecutionResult.Blocked("RETURN_ACTION_PAYLOAD_INVALID", "İade kararı isteği okunamadı; pazaryerine bir şey gönderilmedi. Kararı iade ekranından yeniden verin."));
        }
        var decision = await db.ReturnDecisions.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == decisionId, cancellationToken);
        if (decision is null) throw new JobProcessingException(JobExecutionResult.Blocked("RETURN_DECISION_NOT_FOUND", "İade kararı panelde artık yok; pazaryerine bir şey gönderilmedi. İadeyi yenileyip kararı yeniden verin."));
        if (decision.Status == "SUCCEEDED") return true;
        if (decision.Status == "MANUAL_REVIEW") throw new JobProcessingException(JobExecutionResult.ManualReview(decision.ErrorCode ?? "RETURN_ACTION_REVIEW_REQUIRED", "İade kararı manuel inceleme bekliyor.", decision.ExternalOperationId));
        var claim = await db.ReturnClaims.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == decision.ClaimId && x.ConnectionId == connectionId, cancellationToken);
        if (claim is null) throw new JobProcessingException(JobExecutionResult.Blocked("RETURN_CLAIM_NOT_FOUND", "İade talebi panelde artık yok (müşteri iptal etmiş olabilir); pazaryerine bir şey gönderilmedi."));
        if (!await ExternalWriteAllowedAsync(tenantId, connectionId, MarketplaceExternalWritePolicies.Return, cancellationToken)) throw new JobProcessingException(JobExecutionResult.Blocked("EXTERNAL_WRITE_POLICY_DISABLED", "İade dış yazma akışı kapalı; pazar yeri isteği gönderilmedi."));
        var returnLines = db.ReturnLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.ClaimId == claim.Id);
        if (selectedReturnLineIds is not null) returnLines = returnLines.Where(x => selectedReturnLineIds.Contains(x.Id));
        var lineIds = await returnLines.OrderBy(x => x.Id).Select(x => x.ExternalLineId).ToListAsync(cancellationToken);
        if (lineIds.Count == 0) { decision.Status = "FAILED"; decision.ErrorCode = "RETURN_LINES_REQUIRED"; decision.CompletedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); throw new JobProcessingException(JobExecutionResult.Blocked("RETURN_LINES_REQUIRED", "İade kararında seçili ürün satırı kalmadı; pazaryerine bir şey gönderilmedi. Satırları seçip kararı yeniden verin.")); }
        var evidenceRows = await (from evidence in db.ReturnEvidence.AsNoTracking() where evidence.TenantId == tenantId && evidence.DecisionId == decisionId join asset in db.FileAssets.AsNoTracking() on new { evidence.TenantId, Id = evidence.FileAssetId } equals new { asset.TenantId, asset.Id } select asset).ToListAsync(cancellationToken);
        var evidenceFiles = new List<ReturnEvidenceFile>(); long totalBytes = 0;
        foreach (var asset in evidenceRows)
        {
            if (asset.SizeBytes <= 0 || asset.SizeBytes > 10 * 1024 * 1024 || totalBytes + asset.SizeBytes > 25 * 1024 * 1024) { decision.Status = "FAILED"; decision.ErrorCode = "RETURN_EVIDENCE_LIMIT_EXCEEDED"; decision.CompletedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); throw new JobProcessingException(JobExecutionResult.Blocked("RETURN_EVIDENCE_LIMIT_EXCEEDED", "İade kanıt dosyaları boyut sınırını aşıyor (dosya başına 10 MB, toplam 25 MB); pazaryerine bir şey gönderilmedi.")); }
            await using var source = await files.OpenReadAsync(tenantId, asset.RelativePath, cancellationToken); await using var buffer = new MemoryStream(); await source.CopyToAsync(buffer, cancellationToken); if (buffer.Length != asset.SizeBytes) { decision.Status = "MANUAL_REVIEW"; decision.ErrorCode = "RETURN_EVIDENCE_SIZE_MISMATCH"; decision.CompletedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); throw new JobProcessingException(JobExecutionResult.ManualReview(decision.ErrorCode, "İade kanıt dosyasının kayıtlı boyutu ile okunan içerik eşleşmedi.")); }
            var bytes = buffer.ToArray(); var checksum = Convert.ToHexString(SHA256.HashData(bytes)); if (!string.Equals(checksum, asset.Sha256, StringComparison.OrdinalIgnoreCase)) { decision.Status = "MANUAL_REVIEW"; decision.ErrorCode = "RETURN_EVIDENCE_CHECKSUM_MISMATCH"; decision.CompletedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); throw new JobProcessingException(JobExecutionResult.ManualReview(decision.ErrorCode, "İade kanıt dosyasının checksum doğrulaması başarısız oldu.")); }
            evidenceFiles.Add(new(asset.OriginalNameSafe ?? $"evidence-{asset.Id:N}", asset.MimeType, bytes)); totalBytes += bytes.LongLength;
        }
        var existingEffect = await effectGuard.FindAsync(tenantId, MarketplaceJobTypes.ReturnAction, decision.IdempotencyKey, cancellationToken);
        // A Trendyol exchange request is approved in Trendyol's seller panel only: an approval queued before the claim was
        // known as one is not sent (ReturnCustomerRequestPolicy.ApprovalBlocked); one already sent is left to its effect.
        if (existingEffect is null && decision.Action == "APPROVE"
            && ReturnCustomerRequestPolicy.ApprovalBlocked(await ConnectionPlatformCode(tenantId, connectionId, cancellationToken), claim.CustomerRequest))
        {
            decision.Status = "FAILED"; decision.ErrorCode = ReturnCustomerRequestPolicy.TrendyolExchangeApproveBlockedCode; decision.CompletedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken);
            throw new JobProcessingException(JobExecutionResult.Blocked(ReturnCustomerRequestPolicy.TrendyolExchangeApproveBlockedCode, ReturnCustomerRequestPolicy.TrendyolExchangeApproveBlockedMessage));
        }
        var gate = ExternalEffectGuard.Gate(existingEffect);
        if (gate == ExternalEffectGate.Open) { decision.Status = "MANUAL_REVIEW"; decision.ErrorCode = "EXTERNAL_EFFECT_AMBIGUOUS"; decision.CompletedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); throw new JobProcessingException(JobExecutionResult.ManualReview(decision.ErrorCode, "Önceki iade aksiyonunun sonucu kesinleştirilemedi; tekrar gönderim engellendi.")); }
        // Q-2: a record closed as unresolved does not make the decision submitted. The claim is read once, and
        // that read settles the decision only when it shows the action's outcome; otherwise the job stays in
        // manual review and nothing is sent again.
        if (gate == ExternalEffectGate.Unresolved)
        {
            TrackRequest();
            var check = await returns.GetAsync(Context(tenantId, connectionId, correlationId, $"{decision.IdempotencyKey}:verify"), claim.ExternalClaimId, cancellationToken);
            if (check.IsSuccess) { TrackReceived(); await UpsertReturn(tenantId, connectionId, check.Value!, cancellationToken); }
            else TrackResultFailure(check.Error);
            if (decision.Status == "SUCCEEDED") return true;
            throw new JobProcessingException(JobExecutionResult.ManualReview("EXTERNAL_EFFECT_UNRESOLVED", "Önceki iade aksiyonunun sonucu doğrulanamadan kapatıldı; tekrar gönderilmedi. Pazaryeri panelinden kontrol edin."));
        }
        if (gate == ExternalEffectGate.Done)
        {
            decision.Status = "SUBMITTED"; decision.CompletedAt = null; await db.SaveChangesAsync(cancellationToken);
            TrackRequest();
            var readback = await returns.GetAsync(Context(tenantId, connectionId, correlationId, $"{decision.IdempotencyKey}:readback"), claim.ExternalClaimId, cancellationToken);
            if (readback.IsSuccess) TrackReceived();
            if (readback.IsSuccess) await UpsertReturn(tenantId, connectionId, readback.Value!, cancellationToken);
            else { await RecordIssue(tenantId, $"return-readback:{connectionId}:{claim.ExternalClaimId}:{decision.Action}", "RETURN_ACTION_READBACK_PENDING", "İade aksiyonu daha önce kabul edildi ancak read-back tamamlanamadı; planlı return sync kesinleştirecek.", cancellationToken); await db.SaveChangesAsync(cancellationToken); }
            return true;
        }
        // The claim and action are recorded as the instant return decision does, so the sweep can verify an unclear outcome.
        var effect = effectGuard.New(tenantId, MarketplaceJobTypes.ReturnAction, decision.IdempotencyKey, connectionId, claim.Id, $"RETURN_{decision.Action}");
        var send = await effectGuard.SendAsync(effect, () =>
        {
            TrackRequest();
            return returns.ExecuteAsync(Context(tenantId, connectionId, correlationId, decision.IdempotencyKey), new(claim.ExternalClaimId, lineIds, decision.Action, decision.ReasonCode, decision.Explanation, evidenceFiles), cancellationToken);
        }, cancellationToken);
        var result = send.Result;
        var now = timeProvider.GetUtcNow();
        if (send.Accepted)
        {
            TrackReceived();
            decision.Status = "SUBMITTED"; decision.ExternalOperationId = result.Value!.ExternalOperationId; decision.ErrorCode = null; decision.CompletedAt = null; await db.SaveChangesAsync(cancellationToken);
            TrackRequest();
            var readback = await returns.GetAsync(Context(tenantId, connectionId, correlationId, $"{decision.IdempotencyKey}:readback"), claim.ExternalClaimId, cancellationToken);
            if (readback.IsSuccess) TrackReceived();
            if (readback.IsSuccess) await UpsertReturn(tenantId, connectionId, readback.Value!, cancellationToken);
            else { await RecordIssue(tenantId, $"return-readback:{connectionId}:{claim.ExternalClaimId}:{decision.Action}", "RETURN_ACTION_READBACK_PENDING", "İade aksiyonu kabul edildi ancak anlık read-back tamamlanamadı; planlı return sync kesinleştirecek.", cancellationToken); await db.SaveChangesAsync(cancellationToken); }
            return true;
        }
        var error = send.Error; decision.ErrorCode = error.Code; decision.ExternalOperationId ??= error.RemoteRequestId;
        TrackResultFailure(error);
        switch (send.Outcome)
        {
            case ExternalEffectSendOutcome.Uncertain:
                decision.Status = "MANUAL_REVIEW"; decision.CompletedAt = now; await db.SaveChangesAsync(cancellationToken);
                throw new JobProcessingException(JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS", "İade aksiyonunun uzak tarafta uygulanıp uygulanmadığı kesinleştirilemedi.", error.RemoteRequestId));
            // Never sent (the panel's own circuit is open): the decision stays waiting and the job sends it again later.
            case ExternalEffectSendOutcome.LocalCircuit:
                await effectGuard.ReleaseAsync(effect, cancellationToken);
                throw JobProcessingException.FromAdapter(error);
            // ISLEYICI-4 (15 Nov 2026): a refusal the job retries (a quota answer) was not applied and is sent again, so the
            // decision stays waiting, as an open decision, instead of showing "Başarısız" while the job still sends it (the
            // owner could open a second decision for the same lines meanwhile). Only a final refusal fails the decision.
            case ExternalEffectSendOutcome.Retryable:
                effectGuard.Release(effect); decision.Status = "PENDING"; decision.CompletedAt = null; await db.SaveChangesAsync(cancellationToken);
                throw new JobProcessingException(JobExecutionResult.FromAdapterError(error));
            default:
                effectGuard.Release(effect); decision.Status = "FAILED"; decision.CompletedAt = now; await db.SaveChangesAsync(cancellationToken);
                throw JobProcessingException.FromAdapter(error);
        }
    }
}
