using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.PttAvm;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed partial class MarketplaceJobProcessor
{
    // "Platformlarda Güncelle" on PTT AVM: one UpdateProductsV3 request (SUBMIT_UNAPPROVED), then its tracking result
    // (POLL_UNAPPROVED). The write is manual: the master switch and the connection's MANUAL_WRITE policy are checked
    // before the call, as for the publication. A refusal before the send (a variant PTT AVM holds that cannot be carried,
    // a product PTT AVM does not hold, the switches) marks the card's update, never its listings: what PTT AVM holds did
    // not change. The tracking result answers per product barcode, so a family's parent line answers for each of the
    // card's variants (PttAvmProductPayloadRules.FamilyResultLines).
    private async Task<JobExecutionResult> UpdatePttAvmProduct(Guid tenantId, Guid connectionId, ProductUpdateJobPayload payload, IntegrationJob job, ChannelListingProfile profile, IReadOnlyList<ChannelListingVariant> listings, IReadOnlyDictionary<Guid, MarketplaceListingState> states, string correlationId, CancellationToken cancellationToken)
    {
        var phase = payload.Phase.Trim().ToUpperInvariant();
        if (phase == "SUBMIT_UNAPPROVED")
        {
            if (!await ExternalWriteMasterAllowedAsync(tenantId, connectionId, cancellationToken))
                return await MarkPttAvmUpdate(profile, "UPDATE_BLOCKED", "EXTERNAL_WRITES_DISABLED", JobExecutionResult.Blocked("EXTERNAL_WRITES_DISABLED", "Dış yazma anahtarı kapalı; PTT AVM isteği gönderilmedi."), cancellationToken);
            if (!await PttAvmManualWritesAllowedAsync(tenantId, connectionId, cancellationToken))
                return await MarkPttAvmUpdate(profile, "UPDATE_BLOCKED", PttAvmManualWritePolicy.DisabledCode, JobExecutionResult.Blocked(PttAvmManualWritePolicy.DisabledCode, PttAvmManualWritePolicy.DisabledMessage), cancellationToken);

            var effectType = $"{MarketplaceJobTypes.ProductUpdate}:{phase}";
            var effectKey = $"{job.EffectIdempotencyKey}:{phase}";
            if (await db.ExternalEffectRecords.AnyAsync(x => x.TenantId == tenantId && x.EffectType == effectType && x.IdempotencyKey == effectKey, cancellationToken))
                return await MarkPttAvmUpdate(profile, "MANUAL_REVIEW", "EXTERNAL_EFFECT_AMBIGUOUS", JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS", "Önceki PTT AVM güncelleme çağrısının sonucu kesinleştirilemedi; tekrar gönderim engellendi."), cancellationToken);
            var effect = new ExternalEffectRecord { Id = Guid.CreateVersion7(), TenantId = tenantId, EffectType = effectType, IdempotencyKey = effectKey, CreatedAt = timeProvider.GetUtcNow() };
            db.ExternalEffectRecords.Add(effect); await db.SaveChangesAsync(cancellationToken);

            TrackRequest();
            var publication = new ProductUpdatePublication(payload.ProductId, payload.Mode, payload.PayloadHash, payload.UnapprovedPayloadJson, payload.ApprovedContentPayloadJson, payload.ApprovedVariantPayloadJson, payload.ApprovedDeliveryPayloadJson);
            var submit = await products.UpdateUnapprovedAsync(Context(tenantId, connectionId, correlationId, effectKey), publication, cancellationToken);
            if (!submit.IsSuccess)
            {
                TrackResultFailure(submit.Error);
                var error = submit.Error!;
                if (IsAmbiguous(error)) return await MarkPttAvmUpdate(profile, "MANUAL_REVIEW", "EXTERNAL_EFFECT_AMBIGUOUS", JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS", "PTT AVM güncelleme çağrısının uygulanıp uygulanmadığı kesinleştirilemedi.", error.RemoteRequestId), cancellationToken);
                db.ExternalEffectRecords.Remove(effect); await db.SaveChangesAsync(cancellationToken);
                if (error.Code == PttAvmProductPayloadRules.DryRunCode)
                    return await MarkPttAvmUpdate(profile, "DRY_RUN", error.Code, JobExecutionResult.Blocked(error.Code, error.SafeMessage), cancellationToken);
                var failure = JobExecutionResult.FromAdapterError(error);
                // PTT AVM's five-minute wait for a repeated request holds this request only, not the connection's other jobs.
                if (error.Code == PttAvmProductPayloadRules.RepeatRequestWaitCode) failure = failure with { BackOffConnection = false };
                return await MarkPttAvmUpdate(profile, failure.Kind == JobCompletionKind.Retry ? "UPDATE_QUEUED" : "UPDATE_BLOCKED", error.Code, failure, cancellationToken);
            }
            effect.CompletedAt = timeProvider.GetUtcNow();
            var operation = submit.Value!;
            var poll = payload with { Phase = "POLL_UNAPPROVED", ExternalOperationId = operation.ExternalOperationId, SubmittedAt = operation.SubmittedAt };
            job.PayloadJson = JsonSerializer.Serialize(poll); job.PayloadHash = Hash(job.PayloadJson);
            profile.ActualStatus = "UPDATE_SUBMITTED"; profile.LastRejectionCode = null; profile.Version++;
            await db.SaveChangesAsync(cancellationToken);
            return JobExecutionResult.Retry("PRODUCT_UPDATE_BATCH_PENDING", "PTT AVM ürün güncellemesinin sonucu bekleniyor.", TimeSpan.FromSeconds(15), operation.ExternalOperationId);
        }

        if (phase != "POLL_UNAPPROVED" || string.IsNullOrWhiteSpace(payload.ExternalOperationId) || payload.SubmittedAt is null)
            return JobExecutionResult.Blocked("PRODUCT_UPDATE_PHASE_INVALID", "Ürün güncelleme işi bilinmeyen bir fazda.");
        if (timeProvider.GetUtcNow() - payload.SubmittedAt.Value > TimeSpan.FromHours(4))
            return await MarkPttAvmUpdate(profile, "MANUAL_REVIEW", "PRODUCT_UPDATE_BATCH_EXPIRED", JobExecutionResult.ManualReview("PRODUCT_UPDATE_BATCH_EXPIRED", "PTT AVM güncelleme sonucu dört saatlik pencerede alınamadı.", payload.ExternalOperationId), cancellationToken);

        TrackRequest();
        var operationResult = await products.GetOperationAsync(Context(tenantId, connectionId, correlationId, $"{job.EffectIdempotencyKey}:{phase}:poll"), payload.ExternalOperationId, cancellationToken);
        if (!operationResult.IsSuccess)
        {
            TrackResultFailure(operationResult.Error);
            var result = JobExecutionResult.FromAdapterError(operationResult.Error!);
            return result.Kind == JobCompletionKind.Retry ? result : await MarkPttAvmUpdate(profile, "MANUAL_REVIEW", operationResult.Error!.Code, result, cancellationToken);
        }
        var status = operationResult.Value!;
        if (string.Equals(status.Status, "IN_PROGRESS", StringComparison.OrdinalIgnoreCase))
            return JobExecutionResult.Retry("PRODUCT_UPDATE_BATCH_PENDING", "PTT AVM ürün güncellemesi işleniyor.", ProductUpdatePollDelay(payload.SubmittedAt.Value), payload.ExternalOperationId);
        // PTT AVM cancelled the whole request: nothing was applied.
        if (string.Equals(status.Status, "REJECTED", StringComparison.OrdinalIgnoreCase))
            return await MarkPttAvmUpdate(profile, "UPDATE_REJECTED", "PTTAVM_PRODUCT_UPDATE_REJECTED", JobExecutionResult.Blocked("PTTAVM_PRODUCT_UPDATE_REJECTED", "PTT AVM ürün güncellemesini iptal etti; hiçbir alan değişmedi. Nedeni PTT AVM satıcı panelinde görülebilir.", payload.ExternalOperationId), cancellationToken);
        if (!string.Equals(status.Status, "COMPLETED", StringComparison.OrdinalIgnoreCase))
            return await MarkPttAvmUpdate(profile, "MANUAL_REVIEW", "PRODUCT_UPDATE_BATCH_STATUS_UNKNOWN", JobExecutionResult.ManualReview("PRODUCT_UPDATE_BATCH_STATUS_UNKNOWN", "PTT AVM takip sonucu tanınmayan bir durum döndürdü.", payload.ExternalOperationId), cancellationToken);

        var parent = PttAvmProductUpdateRules.Barcode(payload.UnapprovedPayloadJson);
        var lines = status.Lines.Where(x => !string.IsNullOrWhiteSpace(x.ExternalKey)).GroupBy(x => x.ExternalKey, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);
        var keys = listings.Select(x => x.ExternalBarcode ?? "").ToList();
        var byListing = parent is null ? new Dictionary<string, RemoteOperationLine>(StringComparer.OrdinalIgnoreCase) : PttAvmProductPayloadRules.FamilyResultLines(parent, lines, keys);
        if (keys.Any(string.IsNullOrWhiteSpace) || keys.Any(key => !byListing.ContainsKey(key)))
            return await MarkPttAvmUpdate(profile, "MANUAL_REVIEW", "PRODUCT_UPDATE_BATCH_CONTRACT_INVALID", JobExecutionResult.ManualReview("PRODUCT_UPDATE_BATCH_CONTRACT_INVALID", "PTT AVM takip sonucu kartın barkodları için satır içermiyor; güncellemenin sonucu PTT AVM satıcı panelinden kontrol edilmeli.", payload.ExternalOperationId), cancellationToken);

        var rejected = 0;
        foreach (var listing in listings)
        {
            var line = byListing[listing.ExternalBarcode!];
            var code = line.Succeeded ? null : SafeCode(line.ErrorCode) ?? "PRODUCT_UPDATE_REJECTED";
            if (code is not null) rejected++;
            listing.ActualStatus = code is null ? PttAvmProductUpdateRules.CompletedListingStatus(listing.ActualStatus) : "UPDATE_REJECTED";
            listing.RejectionCode = code;
            if (states.TryGetValue(listing.VariantId, out var state)) { state.ActualStatus = listing.ActualStatus; state.LastRejectionCode = code; state.Version++; }
        }
        profile.ActualStatus = rejected == 0 ? PttAvmProductUpdateRules.CompletedProfileStatus(listings.Select(x => x.ActualStatus).ToList()) : rejected == listings.Count ? "UPDATE_REJECTED" : "UPDATE_PARTIAL_FAILURE";
        profile.LastRejectionCode = listings.Select(x => x.RejectionCode).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        profile.Version++;
        await db.SaveChangesAsync(cancellationToken);
        var reasons = RejectionReasons(listings.Select(listing => byListing[listing.ExternalBarcode!]));
        return rejected == 0 ? JobExecutionResult.Success()
            : rejected == listings.Count ? JobExecutionResult.Blocked("PRODUCT_UPDATE_REJECTED", $"PTT AVM ürün güncellemesini reddetti.{reasons}", payload.ExternalOperationId)
            : JobExecutionResult.Blocked("PRODUCT_UPDATE_PARTIAL_FAILURE", $"PTT AVM ürün güncellemesi bazı varyantlar için reddedildi.{reasons}", payload.ExternalOperationId);
    }

    // The card's update state; its listings keep what PTT AVM holds.
    private async Task<JobExecutionResult> MarkPttAvmUpdate(ChannelListingProfile profile, string status, string? rejectionCode, JobExecutionResult result, CancellationToken cancellationToken)
    {
        profile.ActualStatus = status;
        profile.LastRejectionCode = SafeCode(rejectionCode);
        profile.Version++;
        await db.SaveChangesAsync(cancellationToken);
        return result;
    }
}
