using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed partial class MarketplaceJobProcessor
{
    private async Task<JobExecutionResult> UpdateProduct(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        ProductUpdateJobPayload? payload;
        try { payload = JsonSerializer.Deserialize<ProductUpdateJobPayload>(payloadJson, JsonOptions); }
        catch (JsonException) { return JobExecutionResult.Blocked("PRODUCT_UPDATE_PAYLOAD_INVALID", "Ürün güncelleme işi payload sözleşmesini sağlamıyor."); }
        if (payload is null || payload.JobId == Guid.Empty || payload.ProductId == Guid.Empty || payload.ProfileId == Guid.Empty || string.IsNullOrWhiteSpace(payload.PayloadHash))
            return JobExecutionResult.Blocked("PRODUCT_UPDATE_PAYLOAD_INVALID", "Ürün güncelleme işi zorunlu alanları eksik.");

        var job = await db.IntegrationJobs.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == payload.JobId && x.ConnectionId == connectionId && x.JobType == MarketplaceJobTypes.ProductUpdate, cancellationToken);
        var profile = await db.ChannelListingProfiles.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == payload.ProfileId && x.ProductId == payload.ProductId && x.ConnectionId == connectionId, cancellationToken);
        if (job is null || profile is null) return JobExecutionResult.Blocked("PRODUCT_UPDATE_STATE_MISSING", "Ürün güncelleme işi veya listing profile bulunamadı.");
        // A card of a grouped Trendyol send may have been moved on by the card that sent or polled the shared request
        // since this job was leased: the stored payload is the current one.
        if (payload.SendGroupId is not null && !string.Equals(job.PayloadJson, payloadJson, StringComparison.Ordinal))
        {
            try { payload = JsonSerializer.Deserialize<ProductUpdateJobPayload>(job.PayloadJson, JsonOptions) ?? payload; }
            catch (JsonException) { return JobExecutionResult.Blocked("PRODUCT_UPDATE_PAYLOAD_INVALID", "Ürün güncelleme işi payload sözleşmesini sağlamıyor."); }
        }
        var platformCode = await ConnectionPlatformCode(tenantId, connectionId, cancellationToken);
        // Trendyol: the cards one "Platformlarda Güncelle" sent together share their requests (TrendyolUpdateGroups).
        var groupedTrendyol = payload.SendGroupId is not null && string.Equals(platformCode, "TRENDYOL", StringComparison.OrdinalIgnoreCase);
        var isHepsiburada = string.Equals(platformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase);
        // n11: one product-update task (the content phase) answered per stock code (N11ProductUpdateComposer).
        var isN11 = string.Equals(platformCode, "N11", StringComparison.OrdinalIgnoreCase);

        var listings = await db.ChannelListingVariants.Where(x => x.TenantId == tenantId && x.ProfileId == profile.Id).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        var variantIds = listings.Select(x => x.VariantId).ToArray();
        var states = variantIds.Length == 0 ? new Dictionary<Guid, MarketplaceListingState>() : await db.MarketplaceListingStates.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && variantIds.Contains(x.VariantId)).ToDictionaryAsync(x => x.VariantId, cancellationToken);
        if (listings.Count == 0 || states.Count != listings.Count) return await MarkUpdateResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_UPDATE_STATE_INCOMPLETE", JobExecutionResult.ManualReview("PRODUCT_UPDATE_STATE_INCOMPLETE", "Ürün güncelleme listing state kayıtları eksik."), cancellationToken);
        if (states.Values.Any(x => !string.Equals(x.PayloadHash, payload.PayloadHash, StringComparison.Ordinal))) return JobExecutionResult.Blocked("PRODUCT_UPDATE_SUPERSEDED", "Daha yeni ürün payload'ı bulundu; eski güncelleme işi uzak çağrı yapmadan durduruldu.");
        if (string.Equals(platformCode, "PTTAVM", StringComparison.OrdinalIgnoreCase)) return await UpdatePttAvmProduct(tenantId, connectionId, payload, job, profile, listings, states, correlationId, cancellationToken);

        var phase = payload.Phase.Trim().ToUpperInvariant();
        // The card that polled a shared request answered this card: it completes, or stops with the answer it was given.
        if (phase == TrendyolUpdateGroups.DonePhase) return await CompleteProductUpdate(tenantId, connectionId, payload, profile, listings, states, correlationId, cancellationToken);
        if (phase == TrendyolUpdateGroups.StoppedPhase)
            return payload.GroupOutcome is { } groupOutcome
                ? await MarkUpdateResult(tenantId, connectionId, profile, groupOutcome.ProfileStatus, groupOutcome.RejectionCode, TrendyolUpdateGroups.Result(groupOutcome), cancellationToken)
                : JobExecutionResult.Blocked("PRODUCT_UPDATE_PHASE_INVALID", "Ürün güncelleme işi bilinmeyen bir fazda.");
        if (phase.StartsWith("SUBMIT_", StringComparison.Ordinal))
        {
            if (!await ExternalWriteMasterAllowedAsync(tenantId, connectionId, cancellationToken)) return await MarkUpdateResult(tenantId, connectionId, profile, "UPDATE_BLOCKED", "EXTERNAL_WRITES_DISABLED", JobExecutionResult.Blocked("EXTERNAL_WRITES_DISABLED", "Dış yazma anahtarı kapalı; pazar yeri isteği gönderilmedi."), cancellationToken);
            if (isHepsiburada)
            {
                if (!string.Equals(configuration["Hepsiburada:AuthenticationMode"], "BASIC", StringComparison.OrdinalIgnoreCase)) return await MarkUpdateResult(tenantId, connectionId, profile, "UPDATE_BLOCKED", "HEPSIBURADA_AUTHENTICATION_UNVERIFIED", JobExecutionResult.Blocked("HEPSIBURADA_AUTHENTICATION_UNVERIFIED", "Hepsiburada auth biçimi doğrulanmadı; ürün güncellemesi gönderilmedi."), cancellationToken);
                if (!await HasHepsiburadaPublicationEvidenceAsync(tenantId, connectionId, cancellationToken)) return await MarkUpdateResult(tenantId, connectionId, profile, "UPDATE_BLOCKED", "HEPSIBURADA_WRITE_CAPABILITY_EVIDENCE_REQUIRED", JobExecutionResult.Blocked("HEPSIBURADA_WRITE_CAPABILITY_EVIDENCE_REQUIRED", "Ürün güncellemesi için bu mağaza/ortamda doğrulanmış fiyat ve stok yazma kanıtı bulunamadı; bağlantıyı test edin. Gönderim yapılmadı."), cancellationToken);
            }
            // Trendyol's content request goes per content: each barcode's content is read first (TrendyolContentGroups).
            if (phase == "SUBMIT_CONTENT" && string.Equals(platformCode, "TRENDYOL", StringComparison.OrdinalIgnoreCase)
                && TrendyolContentGroups.NeedsResolution(payload.ApprovedContentPayloadJson))
            {
                var (resolvedContent, contentNotFound, stop) = await ResolveTrendyolContentGroupsAsync(tenantId, connectionId, job, profile, payload.ApprovedContentPayloadJson, correlationId, cancellationToken);
                if (stop is not null) return stop;
                // A barcode with no approved content is left out of the content request: the job's variant results say so
                // (D7) instead of counting it as sent.
                payload = payload with { ApprovedContentPayloadJson = resolvedContent!, ContentNotFoundBarcodes = contentNotFound.Count > 0 ? contentNotFound : null };
                job.PayloadJson = JsonSerializer.Serialize(payload); job.PayloadHash = Hash(job.PayloadJson);
                await db.SaveChangesAsync(cancellationToken);
            }
            var phasePayload = UpdatePayload(payload, phase);
            if (!HasItems(phasePayload))
            {
                var skipped = AdvanceUpdate(payload, phase);
                if (skipped is null) return await CompleteProductUpdate(tenantId, connectionId, payload, profile, listings, states, correlationId, cancellationToken);
                job.PayloadJson = JsonSerializer.Serialize(skipped); job.PayloadHash = Hash(job.PayloadJson);
                await db.SaveChangesAsync(cancellationToken);
                return JobExecutionResult.Retry("PRODUCT_UPDATE_NEXT_PHASE", "Boş ürün güncelleme fazı atlandı.", TimeSpan.FromSeconds(1));
            }

            var effectType = $"{MarketplaceJobTypes.ProductUpdate}:{phase}";
            var effectKey = $"{job.EffectIdempotencyKey}:{phase}";
            // Any record stops the send (open, unresolved or done).
            if (await effectGuard.ExistsAsync(tenantId, effectType, effectKey, cancellationToken))
                return await MarkUpdateResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "EXTERNAL_EFFECT_AMBIGUOUS", JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS", "Önceki ürün güncelleme çağrısının sonucu kesinleştirilemedi; tekrar gönderim engellendi."), cancellationToken);
            if (groupedTrendyol && await SubmitTrendyolGroupAsync(tenantId, connectionId, job, payload, profile, phase, correlationId, cancellationToken) is { } grouped) return grouped;

            var effect = effectGuard.New(tenantId, effectType, effectKey);
            var publication = new ProductUpdatePublication(payload.ProductId, payload.Mode, payload.PayloadHash, payload.UnapprovedPayloadJson, payload.ApprovedContentPayloadJson, payload.ApprovedVariantPayloadJson, payload.ApprovedDeliveryPayloadJson);
            var context = Context(tenantId, connectionId, correlationId, effectKey);
            var send = await effectGuard.SendAsync(effect, () =>
            {
                TrackRequest();
                return SendProductUpdatePhaseAsync(phase, context, publication, cancellationToken);
            }, cancellationToken);
            if (!send.Accepted)
            {
                TrackResultFailure(send.Error);
                var error = send.Error;
                if (send.Outcome == ExternalEffectSendOutcome.Uncertain) return await MarkUpdateResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "EXTERNAL_EFFECT_AMBIGUOUS", JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS", "Ürün güncelleme çağrısının uygulanıp uygulanmadığı kesinleştirilemedi.", error.RemoteRequestId), cancellationToken);
                await effectGuard.ReleaseAsync(effect, cancellationToken);
                // Never sent (the panel's own circuit is open), or refused for now (a quota answer) and sent again by the job's
                // retry: the card keeps its in-progress state (J2, 15 Nov 2026: a quota answer showed UPDATE_BLOCKED while the
                // job still sent it). Only a lasting refusal blocks the card.
                if (send.Outcome is ExternalEffectSendOutcome.LocalCircuit or ExternalEffectSendOutcome.Retryable) return JobExecutionResult.FromAdapterError(error);
                return await MarkUpdateResult(tenantId, connectionId, profile, "UPDATE_BLOCKED", error.Code, JobExecutionResult.FromAdapterError(error), cancellationToken);
            }
            var operation = send.Value;
            var poll = payload with { Phase = phase.Replace("SUBMIT_", "POLL_", StringComparison.Ordinal), ExternalOperationId = operation.ExternalOperationId, SubmittedAt = operation.SubmittedAt, GroupReadySince = null };
            job.PayloadJson = JsonSerializer.Serialize(poll); job.PayloadHash = Hash(job.PayloadJson);
            profile.ActualStatus = phase.Replace("SUBMIT_", "UPDATE_", StringComparison.Ordinal) + "_SUBMITTED"; profile.Version++;
            await db.SaveChangesAsync(cancellationToken);
            return JobExecutionResult.Retry("PRODUCT_UPDATE_BATCH_PENDING", ProductUpdatePendingMessage(isHepsiburada, isN11), ProductUpdatePollDelay(operation.SubmittedAt), operation.ExternalOperationId);
        }

        if (!phase.StartsWith("POLL_", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(payload.ExternalOperationId) || payload.SubmittedAt is null)
            return JobExecutionResult.Blocked("PRODUCT_UPDATE_PHASE_INVALID", "Ürün güncelleme işi bilinmeyen bir fazda.");
        if (groupedTrendyol && payload.GroupLeaderJobId is not null) return await PollTrendyolGroupAsync(tenantId, connectionId, job, payload, profile, listings, states, phase, correlationId, cancellationToken);
        if (timeProvider.GetUtcNow() - payload.SubmittedAt.Value > TimeSpan.FromHours(4))
            return await MarkUpdateResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_UPDATE_BATCH_EXPIRED", JobExecutionResult.ManualReview("PRODUCT_UPDATE_BATCH_EXPIRED", "Ürün güncelleme batch sonucu dört saatlik pencerede alınamadı.", payload.ExternalOperationId), cancellationToken);

        TrackRequest();
        var operationResult = await products.GetOperationAsync(Context(tenantId, connectionId, correlationId, $"{job.EffectIdempotencyKey}:{phase}:poll"), payload.ExternalOperationId, cancellationToken);
        if (!operationResult.IsSuccess)
        {
            TrackResultFailure(operationResult.Error);
            var result = JobExecutionResult.FromAdapterError(operationResult.Error!);
            return result.Kind == JobCompletionKind.Retry ? result : await MarkUpdateResult(tenantId, connectionId, profile, "MANUAL_REVIEW", operationResult.Error!.Code, result, cancellationToken);
        }
        var status = operationResult.Value!;
        if (string.Equals(status.Status, "IN_PROGRESS", StringComparison.OrdinalIgnoreCase)) return JobExecutionResult.Retry("PRODUCT_UPDATE_BATCH_PENDING", ProductUpdatePendingMessage(isHepsiburada, isN11), ProductUpdatePollDelay(payload.SubmittedAt.Value), payload.ExternalOperationId);
        // Hepsiburada's dispatch time upload: an error state that names no SKU applied nothing.
        var hepsiburadaDispatch = isHepsiburada && phase == "POLL_DELIVERY";
        if (hepsiburadaDispatch && string.Equals(status.Status, "REJECTED", StringComparison.OrdinalIgnoreCase))
            return await MarkUpdateResult(tenantId, connectionId, profile, "UPDATE_REJECTED", "HEPSIBURADA_DISPATCH_TIME_REJECTED", JobExecutionResult.Blocked("HEPSIBURADA_DISPATCH_TIME_REJECTED", "Hepsiburada teslimat süresi güncellemesini reddetti.", payload.ExternalOperationId), cancellationToken);
        // n11 dropped the whole task (REJECT without SKU results): nothing was applied.
        if (isN11 && string.Equals(status.Status, "REJECTED", StringComparison.OrdinalIgnoreCase))
            return await MarkUpdateResult(tenantId, connectionId, profile, "UPDATE_REJECTED", "N11_TASK_REJECTED", JobExecutionResult.Blocked("N11_TASK_REJECTED", "N11 ürün güncelleme görevini reddetti; hiçbir stok kodu güncellenmedi.", payload.ExternalOperationId), cancellationToken);
        if (!string.Equals(status.Status, "COMPLETED", StringComparison.OrdinalIgnoreCase)) return await MarkUpdateResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_UPDATE_BATCH_STATUS_UNKNOWN", JobExecutionResult.ManualReview("PRODUCT_UPDATE_BATCH_STATUS_UNKNOWN", "Ürün güncelleme batch servisi tanınmayan durum döndürdü.", payload.ExternalOperationId), cancellationToken);

        var failed = status.Lines.Where(x => !x.Succeeded).ToList();
        if (phase == "POLL_CONTENT" && failed.Count > 0 && !isN11)
            return await MarkUpdateResult(tenantId, connectionId, profile, "UPDATE_REJECTED", SafeCode(failed[0].ErrorCode) ?? "PRODUCT_UPDATE_CONTENT_REJECTED", JobExecutionResult.Blocked("PRODUCT_UPDATE_CONTENT_REJECTED", "Trendyol content güncellemesi reddedildi.", payload.ExternalOperationId), cancellationToken);
        if (hepsiburadaDispatch)
        {
            // Done lists only the refused SKUs (by merchantSku, else hbSku); a SKU it leaves out was applied. A failed upload
            // leaves the SKUs it does not name unconfirmed.
            if (status.UnlistedLinesUnconfirmed)
                return await MarkUpdateResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "HEPSIBURADA_DISPATCH_TIME_UNCONFIRMED", JobExecutionResult.ManualReview("HEPSIBURADA_DISPATCH_TIME_UNCONFIRMED", "Hepsiburada teslimat süresi yüklemesi hata verdi; adı geçmeyen listing'lerin sonucu belirsiz.", payload.ExternalOperationId), cancellationToken);
            var hepsiburadaSkus = await db.MarketplaceVariantLinks.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && variantIds.Contains(x.VariantId)).ToDictionaryAsync(x => x.VariantId, x => x.ExternalId, cancellationToken);
            foreach (var line in failed)
            {
                var listing = listings.FirstOrDefault(x => string.Equals(x.ExternalSku, line.ExternalKey, StringComparison.OrdinalIgnoreCase)
                    || hepsiburadaSkus.TryGetValue(x.VariantId, out var hbSku) && string.Equals(hbSku, line.ExternalKey, StringComparison.OrdinalIgnoreCase));
                if (listing is null)
                    return await MarkUpdateResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_UPDATE_BATCH_CONTRACT_INVALID", JobExecutionResult.ManualReview("PRODUCT_UPDATE_BATCH_CONTRACT_INVALID", "Hepsiburada teslimat süresi sonucu gönderilmemiş bir SKU içeriyor.", payload.ExternalOperationId), cancellationToken);
                var code = SafeCode(line.ErrorCode) ?? "HEPSIBURADA_DISPATCH_TIME_REJECTED";
                listing.ActualStatus = "UPDATE_REJECTED"; listing.RejectionCode = code;
                if (states.TryGetValue(listing.VariantId, out var state)) { state.ActualStatus = ListingSaleStatePolicy.AfterUpdate(state.ActualStatus, "UPDATE_REJECTED"); state.LastRejectionCode = code; state.Version++; }
            }
        }
        else if (phase is "POLL_UNAPPROVED" or "POLL_VARIANTS" or "POLL_DELIVERY" || isN11 && phase == "POLL_CONTENT")
        {
            if (status.Lines.Count == 0) return await MarkUpdateResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_UPDATE_BATCH_CONTRACT_INVALID", JobExecutionResult.ManualReview("PRODUCT_UPDATE_BATCH_CONTRACT_INVALID", "Ürün güncelleme batch sonucu satır içermiyor.", payload.ExternalOperationId), cancellationToken);
            var updateKeys = status.Lines.Where(x => !string.IsNullOrWhiteSpace(x.ExternalKey)).GroupBy(x => x.ExternalKey, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);
            // n11 answers per stock code: the code the variant link holds (what was sent), else the listing's SKU.
            var n11StockCodes = isN11
                ? (await db.MarketplaceVariantLinks.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && variantIds.Contains(x.VariantId)).ToListAsync(cancellationToken))
                    .GroupBy(x => x.VariantId).ToDictionary(group => group.Key, group => group.First().ExternalId.Trim())
                : [];
            if (isHepsiburada)
            {
                var expectedKeys = listings.Select(x => x.ExternalSku).ToArray();
                if (expectedKeys.Any(string.IsNullOrWhiteSpace) || expectedKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != expectedKeys.Length
                    || expectedKeys.Any(key => !updateKeys.ContainsKey(key!)) || updateKeys.Keys.Any(key => !expectedKeys.Contains(key, StringComparer.OrdinalIgnoreCase)))
                    return await MarkUpdateResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_UPDATE_BATCH_CONTRACT_INVALID", JobExecutionResult.ManualReview("PRODUCT_UPDATE_BATCH_CONTRACT_INVALID", "Hepsiburada ürün import sonucu merchantSku satırları beklenen listing'lerle eşleşmiyor.", payload.ExternalOperationId), cancellationToken);
            }
            foreach (var listing in listings)
            {
                var key = isN11 ? n11StockCodes.GetValueOrDefault(listing.VariantId) ?? listing.ExternalSku : isHepsiburada ? listing.ExternalSku : listing.ExternalBarcode;
                if (string.IsNullOrWhiteSpace(key) || !updateKeys.TryGetValue(key, out var line)) continue;
                if (line.Succeeded) continue;
                var code = SafeCode(line.ErrorCode) ?? "PRODUCT_UPDATE_REJECTED";
                listing.ActualStatus = "UPDATE_REJECTED"; listing.RejectionCode = code;
                if (states.TryGetValue(listing.VariantId, out var state)) { state.ActualStatus = ListingSaleStatePolicy.AfterUpdate(state.ActualStatus, "UPDATE_REJECTED"); state.LastRejectionCode = code; state.Version++; }
            }
        }

        var next = AdvanceUpdate(payload, phase);
        if (next is null) return await CompleteProductUpdate(tenantId, connectionId, payload, profile, listings, states, correlationId, cancellationToken);
        job.PayloadJson = JsonSerializer.Serialize(next); job.PayloadHash = Hash(job.PayloadJson);
        profile.ActualStatus = "UPDATE_IN_PROGRESS"; profile.Version++;
        await db.SaveChangesAsync(cancellationToken);
        return JobExecutionResult.Retry("PRODUCT_UPDATE_NEXT_PHASE", $"{ProductUpdatePlatformName(isHepsiburada, isN11)} ürün güncellemesinin sonraki batch fazı hazırlanıyor.", TimeSpan.FromSeconds(1));
    }

    private static string ProductUpdatePlatformName(bool isHepsiburada, bool isN11) => isHepsiburada ? "Hepsiburada" : isN11 ? "N11" : "Trendyol";

    private static string ProductUpdatePendingMessage(bool isHepsiburada, bool isN11) =>
        isHepsiburada ? "Hepsiburada ürün import güncelleme sonucu bekleniyor." : isN11 ? "N11 ürün güncelleme görevinin sonucu bekleniyor." : "Trendyol ürün güncelleme batch sonucu bekleniyor.";

    private async Task<JobExecutionResult> CompleteProductUpdate(Guid tenantId, Guid connectionId, ProductUpdateJobPayload payload, ChannelListingProfile profile, IReadOnlyList<ChannelListingVariant> listings, IReadOnlyDictionary<Guid, MarketplaceListingState> states, string correlationId, CancellationToken cancellationToken)
    {
        var platformCode = await ConnectionPlatformCode(tenantId, connectionId, cancellationToken);
        var isHepsiburada = string.Equals(platformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase);
        var rejected = listings.Count(x => string.Equals(x.ActualStatus, "UPDATE_REJECTED", StringComparison.Ordinal));
        // An update the marketplace applies in place, with no product review behind it (Hepsiburada's dispatch time alone,
        // Pazarama's VAT rate), leaves each listing as it was and starts no approval check.
        if (string.Equals(platformCode, "PAZARAMA", StringComparison.OrdinalIgnoreCase)
            || isHepsiburada && !HasItems(payload.UnapprovedPayloadJson) && !HasItems(payload.ApprovedContentPayloadJson))
        {
            profile.ActualStatus = ProductProfileStatusAfterInPlaceUpdate(listings.Select(x => x.ActualStatus).ToList());
            profile.LastRejectionCode = listings.Select(x => x.RejectionCode).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)); profile.Version++;
            await RecordJobVariantResults(tenantId, payload.JobId, MarketplaceJobTypes.ProductUpdate, listings, null, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return rejected == 0 ? JobExecutionResult.Success() : rejected == listings.Count ? JobExecutionResult.Blocked("PRODUCT_UPDATE_REJECTED", "Ürün güncellemesindeki tüm varyantlar reddedildi.") : JobExecutionResult.Blocked("PRODUCT_UPDATE_PARTIAL_FAILURE", "Ürün güncellemesi bazı varyantlar için reddedildi.");
        }
        foreach (var listing in listings.Where(x => !string.Equals(x.ActualStatus, "UPDATE_REJECTED", StringComparison.Ordinal)))
        {
            listing.ActualStatus = isHepsiburada ? "IMPORT_ACCEPTED" : "APPROVAL_PENDING"; listing.RejectionCode = null;
            if (states.TryGetValue(listing.VariantId, out var state)) { state.ActualStatus = ListingSaleStatePolicy.AfterUpdate(state.ActualStatus, listing.ActualStatus); state.LastRejectionCode = null; state.Version++; }
        }
        profile.ActualStatus = rejected == 0 ? (isHepsiburada ? "IMPORT_ACCEPTED" : "APPROVAL_PENDING") : rejected == listings.Count ? "UPDATE_REJECTED" : "UPDATE_PARTIAL_FAILURE";
        profile.LastRejectionCode = listings.Select(x => x.RejectionCode).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)); profile.Version++;
        // Hepsiburada's approval check reads the product update's own trackingId, not the dispatch time upload that ran after it.
        var approvalOperationId = payload.ProductOperationId
            ?? (payload.ExternalOperationId?.StartsWith(Adapters.Hepsiburada.HepsiburadaHttpClient.ShippingInfoUploadPrefix, StringComparison.Ordinal) == true ? null : payload.ExternalOperationId);
        if (rejected < listings.Count) await EnsureApprovalReconciliationJob(tenantId, connectionId, payload.ProductId, profile.Id, payload.PayloadHash, correlationId, cancellationToken, approvalOperationId);
        var notSentBarcodes = JobVariantResultPolicy.NotSentBarcodes(payload.ContentNotFoundBarcodes,
            TrendyolContentGroups.Barcodes(payload.UnapprovedPayloadJson).Concat(TrendyolContentGroups.Barcodes(payload.ApprovedVariantPayloadJson)).Concat(TrendyolContentGroups.Barcodes(payload.ApprovedDeliveryPayloadJson)));
        var notSent = listings.Where(x => x.ExternalBarcode is { } barcode && notSentBarcodes.Contains(barcode.Trim()))
            .DistinctBy(x => x.VariantId)
            .ToDictionary(x => x.VariantId, _ => JobVariantResultPolicy.ApprovedContentNotFoundReason);
        await RecordJobVariantResults(tenantId, payload.JobId, MarketplaceJobTypes.ProductUpdate, listings, null, cancellationToken, notSent: notSent.Count > 0 ? notSent : null);
        await db.SaveChangesAsync(cancellationToken);
        var platformName = ProductUpdatePlatformName(isHepsiburada, string.Equals(platformCode, "N11", StringComparison.OrdinalIgnoreCase));
        return rejected == 0 ? JobExecutionResult.Success() : rejected == listings.Count ? JobExecutionResult.Blocked("PRODUCT_UPDATE_REJECTED", $"{platformName} ürün güncellemesindeki tüm varyantlar reddedildi.") : JobExecutionResult.Blocked("PRODUCT_UPDATE_PARTIAL_FAILURE", $"{platformName} ürün güncellemesi bazı varyantlar için reddedildi.");
    }

    // Pazarama answers a sale status change for each barcode at once, so one call carries the whole job.
    private async Task<JobExecutionResult> ChangePazaramaSaleStatus(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        PazaramaSaleStatusJobPayload? payload;
        try { payload = JsonSerializer.Deserialize<PazaramaSaleStatusJobPayload>(payloadJson, JsonOptions); }
        catch (JsonException) { return JobExecutionResult.Blocked("PAZARAMA_SALE_STATUS_PAYLOAD_INVALID", "Satışa açma/kapatma işi içeriği okunamadı."); }
        if (payload is null || payload.JobId == Guid.Empty || payload.SaleStatusBarcodes.Count is < 1 or > 100) return JobExecutionResult.Blocked("PAZARAMA_SALE_STATUS_PAYLOAD_INVALID", "Satışa açma/kapatma işi barkodları eksik.");
        var job = await db.IntegrationJobs.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == payload.JobId && x.ConnectionId == connectionId && x.JobType == MarketplaceJobTypes.ProductArchive, cancellationToken);
        if (job is null) return JobExecutionResult.Blocked("PAZARAMA_SALE_STATUS_STATE_MISSING", "Satışa açma/kapatma işi bulunamadı.");
        if (!await ExternalWriteMasterAllowedAsync(tenantId, connectionId, cancellationToken)) return JobExecutionResult.Blocked("EXTERNAL_WRITES_DISABLED", "Dış yazma anahtarı kapalı; Pazarama isteği gönderilmedi.");
        var effectKey = job.EffectIdempotencyKey;
        // Any record stops the send (open, unresolved or done).
        if (await effectGuard.ExistsAsync(tenantId, MarketplaceJobTypes.ProductArchive, effectKey, cancellationToken))
            return JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS", "Önceki satışa açma/kapatma çağrısının sonucu kesinleştirilemedi; tekrar gönderim engellendi.");
        var effect = effectGuard.New(tenantId, MarketplaceJobTypes.ProductArchive, effectKey);
        var items = payload.SaleStatusBarcodes.Select(code => new { barcode = code, archived = payload.Close }).ToArray();
        var send = await effectGuard.SendAsync(effect, () =>
        {
            TrackRequest();
            return products.ArchiveAsync(Context(tenantId, connectionId, correlationId, effectKey), JsonSerializer.Serialize(new { items }), cancellationToken);
        }, cancellationToken);
        var submit = send.Result;
        if (!send.Accepted)
        {
            TrackResultFailure(send.Error);
            if (send.Outcome == ExternalEffectSendOutcome.Uncertain) return JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS", "Satışa açma/kapatma çağrısının uygulanıp uygulanmadığı kesinleştirilemedi.", send.Error.RemoteRequestId);
            // The panel's own open circuit is not told apart here: the job retries it like a quota answer.
            await effectGuard.ReleaseAsync(effect, cancellationToken);
            return JobExecutionResult.FromAdapterError(send.Error);
        }
        await db.SaveChangesAsync(cancellationToken);
        TrackRequest();
        var outcome = await products.GetOperationAsync(Context(tenantId, connectionId, correlationId, $"{effectKey}:result"), submit.Value!.ExternalOperationId, cancellationToken);
        if (!outcome.IsSuccess) { TrackResultFailure(outcome.Error); return JobExecutionResult.ManualReview("PAZARAMA_SALE_STATUS_RESULT_UNKNOWN", "Satışa açma/kapatma istendi ama sonucu okunamadı; ürünleri Pazarama panelinden kontrol edin.", submit.Value.ExternalOperationId); }
        var succeededCodes = outcome.Value!.Lines.Where(x => x.Succeeded).Select(x => x.ExternalKey).ToList();
        var failed = outcome.Value.Lines.Where(x => !x.Succeeded).ToList();
        if (succeededCodes.Count > 0)
        {
            // A product closed for sale takes no price or stock updates; opening it brings its offer back.
            var variantIds = await db.MarketplaceVariantLinks.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && succeededCodes.Contains(x.ExternalId)).Select(x => x.VariantId).ToListAsync(cancellationToken);
            var offers = await db.ChannelOffers.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && variantIds.Contains(x.VariantId)).ToListAsync(cancellationToken);
            foreach (var offer in offers) { offer.Status = payload.Close ? "INACTIVE" : "ACTIVE"; offer.Version++; }
            await db.SaveChangesAsync(cancellationToken);
        }
        foreach (var line in failed)
            await RecordIssue(tenantId, $"pazarama-sale-status:{connectionId:N}:{line.ExternalKey}", SafeCode(line.ErrorCode) ?? "PAZARAMA_SALE_STATUS_REJECTED", $"Pazarama satışa {(payload.Close ? "kapatma" : "açma")} işlemini reddetti: {line.ExternalKey}{(string.IsNullOrWhiteSpace(line.Status) ? "" : " · " + line.Status)}", cancellationToken);
        if (failed.Count == 0) return JobExecutionResult.Success();
        return succeededCodes.Count == 0
            ? JobExecutionResult.Blocked("PAZARAMA_SALE_STATUS_REJECTED", "Pazarama satışa açma/kapatma isteğindeki tüm barkodları reddetti.", submit.Value.ExternalOperationId)
            : JobExecutionResult.Blocked("PAZARAMA_SALE_STATUS_PARTIAL_FAILURE", "Pazarama satışa açma/kapatma isteği kısmi başarısızlıkla tamamlandı.", submit.Value.ExternalOperationId);
    }

    private async Task<JobExecutionResult> ArchiveProduct(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        if (payloadJson.Contains("\"SaleStatusBarcodes\"", StringComparison.Ordinal))
            return await ChangePazaramaSaleStatus(tenantId, connectionId, payloadJson, correlationId, cancellationToken);
        ProductArchiveJobPayload? payload;
        try { payload = JsonSerializer.Deserialize<ProductArchiveJobPayload>(payloadJson, JsonOptions); }
        catch (JsonException) { return JobExecutionResult.Blocked("PRODUCT_ARCHIVE_PAYLOAD_INVALID", "Ürün arşiv işi payload sözleşmesini sağlamıyor."); }
        if (payload is null || payload.JobId == Guid.Empty || payload.ProductId == Guid.Empty || payload.ProfileId == Guid.Empty || string.IsNullOrWhiteSpace(payload.PayloadHash) || payload.DeadlineAt <= payload.StartedAt)
            return JobExecutionResult.Blocked("PRODUCT_ARCHIVE_PAYLOAD_INVALID", "Ürün arşiv işi zorunlu alanları eksik.");
        var job = await db.IntegrationJobs.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == payload.JobId && x.ConnectionId == connectionId && x.JobType == MarketplaceJobTypes.ProductArchive, cancellationToken);
        var profile = await db.ChannelListingProfiles.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == payload.ProfileId && x.ProductId == payload.ProductId && x.ConnectionId == connectionId, cancellationToken);
        if (job is null || profile is null) return JobExecutionResult.Blocked("PRODUCT_ARCHIVE_STATE_MISSING", "Ürün arşiv işi veya listing profile bulunamadı.");
        var listings = await db.ChannelListingVariants.Where(x => x.TenantId == tenantId && x.ProfileId == profile.Id).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        var ids = listings.Select(x => x.VariantId).ToArray();
        var states = ids.Length == 0 ? new Dictionary<Guid, MarketplaceListingState>() : await db.MarketplaceListingStates.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && ids.Contains(x.VariantId)).ToDictionaryAsync(x => x.VariantId, cancellationToken);
        if (listings.Count == 0 || states.Count != listings.Count) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_ARCHIVE_STATE_INCOMPLETE", JobExecutionResult.ManualReview("PRODUCT_ARCHIVE_STATE_INCOMPLETE", "Arşiv uzlaştırması için listing state kayıtları eksik."), cancellationToken);
        if (states.Values.Any(x => !string.Equals(x.PayloadHash, payload.PayloadHash, StringComparison.Ordinal))) return JobExecutionResult.Blocked("PRODUCT_ARCHIVE_SUPERSEDED", "Daha yeni listing işlemi bulundu; eski arşiv işi uzak çağrı yapmadan durduruldu.");
        if (timeProvider.GetUtcNow() > payload.DeadlineAt) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_ARCHIVE_DEADLINE_EXPIRED", JobExecutionResult.ManualReview("PRODUCT_ARCHIVE_DEADLINE_EXPIRED", "Ürün arşiv durumu belirlenen pencerede kesinleşmedi."), cancellationToken);

        var phase = payload.Phase.Trim().ToUpperInvariant();
        if (phase == "SUBMIT")
        {
            if (!await ExternalWriteMasterAllowedAsync(tenantId, connectionId, cancellationToken)) return await MarkPublicationResult(tenantId, connectionId, profile, "ARCHIVE_BLOCKED", "EXTERNAL_WRITES_DISABLED", JobExecutionResult.Blocked("EXTERNAL_WRITES_DISABLED", "Dış yazma anahtarı kapalı; pazar yeri isteği gönderilmedi."), cancellationToken);
            var effectKey = job.EffectIdempotencyKey;
            // Any record stops the send (open, unresolved or done).
            if (await effectGuard.ExistsAsync(tenantId, MarketplaceJobTypes.ProductArchive, effectKey, cancellationToken)) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "EXTERNAL_EFFECT_AMBIGUOUS", JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS", "Önceki arşiv çağrısının sonucu kesinleştirilemedi."), cancellationToken);
            var effect = effectGuard.New(tenantId, MarketplaceJobTypes.ProductArchive, effectKey);
            var send = await effectGuard.SendAsync(effect, () =>
            {
                TrackRequest();
                return products.ArchiveAsync(Context(tenantId, connectionId, correlationId, effectKey), payload.PayloadJson, cancellationToken);
            }, cancellationToken);
            if (!send.Accepted)
            {
                TrackResultFailure(send.Error);
                if (send.Outcome == ExternalEffectSendOutcome.Uncertain) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "EXTERNAL_EFFECT_AMBIGUOUS", JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS", "Arşiv çağrısının uzak tarafta uygulanıp uygulanmadığı kesinleştirilemedi.", send.Error.RemoteRequestId), cancellationToken);
                await effectGuard.ReleaseAsync(effect, cancellationToken);
                // J2 (15 Nov 2026): a quota answer is sent again by the job's retry, so the card keeps its state as for the
                // panel's own open circuit; only a lasting refusal shows ARCHIVE_BLOCKED.
                if (send.Outcome is ExternalEffectSendOutcome.LocalCircuit or ExternalEffectSendOutcome.Retryable) return JobExecutionResult.FromAdapterError(send.Error);
                return await MarkPublicationResult(tenantId, connectionId, profile, "ARCHIVE_BLOCKED", send.Error.Code, JobExecutionResult.FromAdapterError(send.Error), cancellationToken);
            }
            var op = send.Value;
            var next = payload with { Phase = "POLL", ExternalOperationId = op.ExternalOperationId };
            job.PayloadJson = JsonSerializer.Serialize(next); job.PayloadHash = Hash(job.PayloadJson); profile.ActualStatus = "ARCHIVE_BATCH_SUBMITTED"; profile.Version++;
            await db.SaveChangesAsync(cancellationToken);
            return JobExecutionResult.Retry("PRODUCT_ARCHIVE_BATCH_PENDING", "Trendyol arşiv batch sonucu bekleniyor.", TimeSpan.FromSeconds(15), op.ExternalOperationId);
        }
        if (phase == "POLL")
        {
            if (string.IsNullOrWhiteSpace(payload.ExternalOperationId)) return JobExecutionResult.Blocked("PRODUCT_ARCHIVE_PHASE_INVALID", "Arşiv poll fazında batch kimliği eksik.");
            TrackRequest();
            var result = await products.GetOperationAsync(Context(tenantId, connectionId, correlationId, $"{job.EffectIdempotencyKey}:poll"), payload.ExternalOperationId, cancellationToken);
            if (!result.IsSuccess) { TrackResultFailure(result.Error); return JobExecutionResult.FromAdapterError(result.Error!); }
            TrackReceived();
            var remote = result.Value!;
            if (remote.Status.Equals("IN_PROGRESS", StringComparison.OrdinalIgnoreCase)) return JobExecutionResult.Retry("PRODUCT_ARCHIVE_BATCH_PENDING", "Trendyol arşiv batch sonucu bekleniyor.", TimeSpan.FromSeconds(20), payload.ExternalOperationId);
            if (!remote.Status.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase)) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_ARCHIVE_BATCH_STATUS_UNKNOWN", JobExecutionResult.ManualReview("PRODUCT_ARCHIVE_BATCH_STATUS_UNKNOWN", "Arşiv batch servisi tanınmayan durum döndürdü.", payload.ExternalOperationId), cancellationToken);
            if (remote.Lines.Count == 0) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_ARCHIVE_BATCH_CONTRACT_INVALID", JobExecutionResult.ManualReview("PRODUCT_ARCHIVE_BATCH_CONTRACT_INVALID", "Arşiv batch sonucu satır içermiyor.", payload.ExternalOperationId), cancellationToken);
            var byBarcode = remote.Lines.Where(x => !string.IsNullOrWhiteSpace(x.ExternalKey)).GroupBy(x => x.ExternalKey, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);
            foreach (var listing in listings)
            {
                if (string.IsNullOrWhiteSpace(listing.ExternalBarcode) || !byBarcode.TryGetValue(listing.ExternalBarcode, out var line)) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_ARCHIVE_BATCH_CONTRACT_INVALID", JobExecutionResult.ManualReview("PRODUCT_ARCHIVE_BATCH_CONTRACT_INVALID", "Arşiv batch sonucu tüm barkodları içermiyor.", payload.ExternalOperationId), cancellationToken);
                var status = line.Succeeded ? (payload.Archived ? "ARCHIVE_ACCEPTED" : "UNARCHIVE_ACCEPTED") : "ARCHIVE_REJECTED";
                var code = line.Succeeded ? null : SafeCode(line.ErrorCode) ?? "PRODUCT_ARCHIVE_REJECTED";
                listing.ActualStatus = status; listing.RejectionCode = code;
                if (states.TryGetValue(listing.VariantId, out var state)) { state.ActualStatus = status; state.LastRejectionCode = code; state.Version++; }
            }
            var reconcile = payload with { Phase = "RECONCILE" };
            job.PayloadJson = JsonSerializer.Serialize(reconcile); job.PayloadHash = Hash(job.PayloadJson); profile.ActualStatus = "ARCHIVE_RECONCILING"; profile.Version++;
            await db.SaveChangesAsync(cancellationToken);
            return JobExecutionResult.Retry("PRODUCT_ARCHIVE_RECONCILE_PENDING", "Trendyol arşiv durumu read-back ile doğrulanacak.", TimeSpan.FromSeconds(30));
        }
        if (phase != "RECONCILE") return JobExecutionResult.Blocked("PRODUCT_ARCHIVE_PHASE_INVALID", "Ürün arşiv işi bilinmeyen bir fazda.");

        var pending = 0; var succeeded = 0; var rejected = listings.Count(x => x.ActualStatus == "ARCHIVE_REJECTED"); string? firstCode = listings.Select(x => x.RejectionCode).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        foreach (var listing in listings.Where(x => x.ActualStatus != "ARCHIVE_REJECTED"))
        {
            if (string.IsNullOrWhiteSpace(listing.ExternalBarcode)) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "REMOTE_BARCODE_REQUIRED", JobExecutionResult.ManualReview("REMOTE_BARCODE_REQUIRED", "Arşiv read-back için barkod eksik."), cancellationToken);
            TrackRequest();
            var result = await products.GetPublicationStatusAsync(Context(tenantId, connectionId, correlationId, $"archive-readback:{profile.Id:N}:{listing.ExternalBarcode}"), listing.ExternalBarcode, cancellationToken);
            if (!result.IsSuccess) { TrackResultFailure(result.Error); var mapped = JobExecutionResult.FromAdapterError(result.Error!); if (mapped.Kind == JobCompletionKind.Retry) return mapped; return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", result.Error!.Code, JobExecutionResult.ManualReview(result.Error.Code, result.Error.SafeMessage, result.Error.RemoteRequestId), cancellationToken); }
            TrackReceived();
            var desiredReached = payload.Archived ? result.Value!.Status == "ARCHIVED" : result.Value!.Status == "APPROVED";
            if (desiredReached)
            {
                var status = payload.Archived ? "ARCHIVED" : "LIVE"; listing.ActualStatus = status; listing.RejectionCode = null;
                if (states.TryGetValue(listing.VariantId, out var state)) { state.ActualStatus = status; state.LastRejectionCode = null; state.Version++; }
                if (!payload.Archived) await EnsureLiveListingLink(tenantId, connectionId, listing, null, createIfMissing: false, cancellationToken);
                succeeded++;
            }
            else if (result.Value!.Status is "NOT_FOUND" or "PENDING_APPROVAL" || (payload.Archived && result.Value.Status == "APPROVED") || (!payload.Archived && result.Value.Status == "ARCHIVED")) pending++;
            else { listing.ActualStatus = "MANUAL_REVIEW"; listing.RejectionCode = "PRODUCT_ARCHIVE_READBACK_CONFLICT"; if (states.TryGetValue(listing.VariantId, out var state)) { state.ActualStatus = "MANUAL_REVIEW"; state.LastRejectionCode = listing.RejectionCode; state.Version++; } firstCode ??= listing.RejectionCode; rejected++; }
        }
        profile.DesiredStatus = payload.Archived ? "ARCHIVED" : "LIVE";
        profile.ActualStatus = pending > 0 ? (succeeded > 0 ? "ARCHIVE_PARTIAL_PENDING" : "ARCHIVE_RECONCILING") : rejected == 0 ? (payload.Archived ? "ARCHIVED" : "LIVE") : succeeded == 0 ? "ARCHIVE_REJECTED" : "ARCHIVE_PARTIAL_FAILURE";
        profile.LastRejectionCode = firstCode; profile.Version++; await db.SaveChangesAsync(cancellationToken);
        if (pending > 0) return JobExecutionResult.Retry("PRODUCT_ARCHIVE_RECONCILE_PENDING", "Trendyol arşiv durumu henüz kesinleşmedi.", TimeSpan.FromMinutes(2));
        if (rejected == 0) return JobExecutionResult.Success();
        return succeeded == 0 ? JobExecutionResult.Blocked("PRODUCT_ARCHIVE_REJECTED", "Trendyol arşiv işlemi tüm varyantlar için başarısız oldu.") : JobExecutionResult.Blocked("PRODUCT_ARCHIVE_PARTIAL_FAILURE", "Trendyol arşiv işlemi bazı varyantlar için başarısız oldu.");
    }

    // The product update request of a SUBMIT_* phase, for a card alone or a grouped Trendyol send.
    private Task<AdapterResult<RemoteOperationRef>> SendProductUpdatePhaseAsync(string phase, AdapterContext context, ProductUpdatePublication publication, CancellationToken cancellationToken) => phase switch
    {
        "SUBMIT_UNAPPROVED" => products.UpdateUnapprovedAsync(context, publication, cancellationToken),
        "SUBMIT_CONTENT" => products.UpdateApprovedContentAsync(context, publication, cancellationToken),
        "SUBMIT_VARIANTS" => products.UpdateApprovedVariantsAsync(context, publication, cancellationToken),
        "SUBMIT_DELIVERY" => products.UpdateApprovedDeliveryAsync(context, publication, cancellationToken),
        _ => Task.FromResult(AdapterResult<RemoteOperationRef>.Failure(new(AdapterErrorClass.ContractViolation, "PRODUCT_UPDATE_PHASE_INVALID", "Ürün güncelleme fazı tanınmıyor.", null, null, null)))
    };
    // An n11 task body carries its SKUs under payload.skus.
    internal static bool HasItems(string payloadJson) { try { using var doc = JsonDocument.Parse(payloadJson); var root = doc.RootElement; return root.ValueKind == JsonValueKind.Array ? root.GetArrayLength() > 0 : root.ValueKind == JsonValueKind.Object && (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0 || root.TryGetProperty("payload", out var n11) && n11.ValueKind == JsonValueKind.Object && n11.TryGetProperty("skus", out var skus) && skus.ValueKind == JsonValueKind.Array && skus.GetArrayLength() > 0); } catch (JsonException) { return false; } }
    private static string UpdatePayload(ProductUpdateJobPayload payload, string phase) => phase switch { "SUBMIT_UNAPPROVED" or "POLL_UNAPPROVED" => payload.UnapprovedPayloadJson, "SUBMIT_CONTENT" or "POLL_CONTENT" => payload.ApprovedContentPayloadJson, "SUBMIT_VARIANTS" or "POLL_VARIANTS" => payload.ApprovedVariantPayloadJson, "SUBMIT_DELIVERY" or "POLL_DELIVERY" => payload.ApprovedDeliveryPayloadJson, _ => "{}" };
    // The card's status after an update applied in place: its listings' own status when they share one (Yayında stays
    // Yayında), the rejection when the update was refused, else what the listings say together.
    internal static string ProductProfileStatusAfterInPlaceUpdate(IReadOnlyList<string?> listingStatuses)
    {
        var rejected = listingStatuses.Count(x => string.Equals(x, "UPDATE_REJECTED", StringComparison.Ordinal));
        if (rejected > 0) return rejected == listingStatuses.Count ? "UPDATE_REJECTED" : "UPDATE_PARTIAL_FAILURE";
        var distinct = listingStatuses.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToList();
        if (distinct.Count == 1 && distinct[0] is { } shared && listingStatuses.All(x => !string.IsNullOrWhiteSpace(x))) return shared;
        return listingStatuses.Any(x => string.Equals(x, "LIVE", StringComparison.Ordinal)) ? "PARTIAL_LIVE" : "MANUAL_REVIEW";
    }

    internal static ProductUpdateJobPayload? AdvanceUpdate(ProductUpdateJobPayload payload, string phase)
    {
        // A full update ends with the delivery phase as well: a Trendyol one carries no delivery items (skipped without a
        // call), a Hepsiburada one may carry the dispatch time upload. The product request's id is kept for the approval check.
        var next = phase switch
        {
            "SUBMIT_UNAPPROVED" or "POLL_UNAPPROVED" => "SUBMIT_DELIVERY",
            "SUBMIT_CONTENT" or "POLL_CONTENT" => "SUBMIT_VARIANTS",
            "SUBMIT_VARIANTS" or "POLL_VARIANTS" => "SUBMIT_DELIVERY",
            "SUBMIT_DELIVERY" or "POLL_DELIVERY" => null,
            _ => null
        };
        var productOperationId = phase is "POLL_UNAPPROVED" or "POLL_CONTENT" ? payload.ExternalOperationId : payload.ProductOperationId;
        return next is null ? null : payload with { Phase = next, ExternalOperationId = null, SubmittedAt = null, ProductOperationId = productOperationId, GroupReadySince = null, GroupLeaderJobId = null };
    }

    private TimeSpan ProductUpdatePollDelay(DateTimeOffset submittedAt)
    {
        var now = timeProvider.GetUtcNow();
        return ProductUpdatePollingPolicy.Delay(
            submittedAt,
            now,
            TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("MarketplaceSync:ProductUpdate:FirstWindowSeconds", 600), 60, 86_400)),
            TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("MarketplaceSync:ProductUpdate:SecondWindowSeconds", 1_800), 120, 172_800)),
            TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("MarketplaceSync:ProductUpdate:ThirdWindowSeconds", 3_600), 180, 259_200)),
            TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("MarketplaceSync:ProductUpdate:FirstDelaySeconds", 120), 1, 3_600)),
            TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("MarketplaceSync:ProductUpdate:SecondDelaySeconds", 300), 1, 3_600)),
            TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("MarketplaceSync:ProductUpdate:ThirdDelaySeconds", 900), 1, 7_200)),
            TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("MarketplaceSync:ProductUpdate:FinalDelaySeconds", 1_800), 1, 14_400)));
    }
}
