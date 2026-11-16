using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.PttAvm;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed partial class MarketplaceJobProcessor
{
    // Every create job that ends leaves its per-variant answer: a path that stops before the batch result (blocked,
    // manual review, expired window) records each sent variant from the card's state, with the job's reason, so the job
    // screen never falls back to "ran before it saved results" for a job that ran after they existed.
    private async Task<JobExecutionResult> CreateProduct(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        var result = await CreateProductCore(tenantId, connectionId, payloadJson, correlationId, cancellationToken);
        if (result.Kind != JobCompletionKind.Retry) await EnsureCreateJobVariantResults(tenantId, connectionId, payloadJson, result, cancellationToken);
        return result;
    }

    private async Task EnsureCreateJobVariantResults(Guid tenantId, Guid connectionId, string payloadJson, JobExecutionResult result, CancellationToken cancellationToken)
    {
        ProductPublicationJobPayload? payload;
        try { payload = JsonSerializer.Deserialize<ProductPublicationJobPayload>(payloadJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (JsonException) { return; }
        if (payload is null || payload.JobId == Guid.Empty || payload.ProfileId == Guid.Empty || string.IsNullOrWhiteSpace(payload.PayloadHash)) return;
        if (await db.JobVariantResults.AnyAsync(x => x.TenantId == tenantId && x.JobId == payload.JobId, cancellationToken)) return;
        var listings = await db.ChannelListingVariants.Where(x => x.TenantId == tenantId && x.ProfileId == payload.ProfileId).ToListAsync(cancellationToken);
        if (listings.Count == 0) return;
        var variantIds = listings.Select(x => x.VariantId).ToArray();
        var states = await db.MarketplaceListingStates.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && variantIds.Contains(x.VariantId)).ToDictionaryAsync(x => x.VariantId, cancellationToken);
        var reason = JobVariantResultPolicy.Reason([result.ErrorSummary]);
        await RecordJobVariantResults(tenantId, payload.JobId, MarketplaceJobTypes.ProductCreate, listings, listings.ToDictionary(x => x.VariantId, _ => reason), cancellationToken, payload.PayloadHash, states);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<JobExecutionResult> CreateProductCore(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        ProductPublicationJobPayload? payload;
        try { payload = JsonSerializer.Deserialize<ProductPublicationJobPayload>(payloadJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (JsonException) { return JobExecutionResult.Blocked("PRODUCT_PUBLICATION_PAYLOAD_INVALID", "Ürün yayınlama işi payload sözleşmesini sağlamıyor."); }
        if (payload is null || payload.JobId == Guid.Empty || payload.ProductId == Guid.Empty || payload.ProfileId == Guid.Empty || string.IsNullOrWhiteSpace(payload.PayloadHash) || string.IsNullOrWhiteSpace(payload.PayloadJson)) return JobExecutionResult.Blocked("PRODUCT_PUBLICATION_PAYLOAD_INVALID", "Ürün yayınlama işi zorunlu alanları eksik.");

        var job = await db.IntegrationJobs.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == payload.JobId && x.ConnectionId == connectionId && x.JobType == MarketplaceJobTypes.ProductCreate, cancellationToken);
        var profile = await db.ChannelListingProfiles.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == payload.ProfileId && x.ProductId == payload.ProductId && x.ConnectionId == connectionId, cancellationToken);
        if (job is null || profile is null) return JobExecutionResult.Blocked("PRODUCT_PUBLICATION_STATE_MISSING", "Yayın işi veya listing profile bulunamadı.");
        var platformCode = await ConnectionPlatformCode(tenantId, connectionId, cancellationToken);

        if (string.Equals(payload.Phase, "SUBMIT", StringComparison.OrdinalIgnoreCase))
        {
            if (!await ExternalWriteMasterAllowedAsync(tenantId, connectionId, cancellationToken)) return await MarkPublicationResult(tenantId, connectionId, profile, "BLOCKED", "EXTERNAL_WRITES_DISABLED", JobExecutionResult.Blocked("EXTERNAL_WRITES_DISABLED", "Dış yazma anahtarı kapalı; pazar yeri isteği gönderilmedi."), cancellationToken, payload.PayloadHash);
            if (PttAvmManualWritePolicy.Applies(platformCode) && !await PttAvmManualWritesAllowedAsync(tenantId, connectionId, cancellationToken))
                return await MarkPublicationResult(tenantId, connectionId, profile, "BLOCKED", PttAvmManualWritePolicy.DisabledCode, JobExecutionResult.Blocked(PttAvmManualWritePolicy.DisabledCode, PttAvmManualWritePolicy.DisabledMessage), cancellationToken, payload.PayloadHash);
            if (string.Equals(platformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(configuration["Hepsiburada:AuthenticationMode"], "BASIC", StringComparison.OrdinalIgnoreCase)) return await MarkPublicationResult(tenantId, connectionId, profile, "BLOCKED", "HEPSIBURADA_AUTHENTICATION_UNVERIFIED", JobExecutionResult.Blocked("HEPSIBURADA_AUTHENTICATION_UNVERIFIED", "Hepsiburada auth biçimi doğrulanmadı; ürün aktarımı gönderilmedi."), cancellationToken, payload.PayloadHash);
                if (!await HasHepsiburadaPublicationEvidenceAsync(tenantId, connectionId, cancellationToken)) return await MarkPublicationResult(tenantId, connectionId, profile, "BLOCKED", "HEPSIBURADA_WRITE_CAPABILITY_EVIDENCE_REQUIRED", JobExecutionResult.Blocked("HEPSIBURADA_WRITE_CAPABILITY_EVIDENCE_REQUIRED", "Bu mağaza/ortamda doğrulanmış fiyat ve stok yazma kanıtı bulunamadı; bağlantıyı test edin. Ürün aktarımı gönderilmedi."), cancellationToken, payload.PayloadHash);
            }
            // Pazarama shows a family's colours in the order it created them: the cards of one round go out one by one
            // in the panel's colour order, each after the earlier cards have their answer.
            if (string.Equals(platformCode, "PAZARAMA", StringComparison.OrdinalIgnoreCase)
                && await PazaramaFamilyOrder.WaitAsync(db, tenantId, connectionId, job, payload, timeProvider.GetUtcNow(), cancellationToken) is { } familyOrderWait)
                return familyOrderWait;
            // A Shopify product carries the whole model family and productSet deletes the variants it is not sent, so a
            // job composed before the family's variants changed must not write its older variant list.
            if (string.Equals(platformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase))
            {
                var current = await new ShopifyProductPublicationComposer(db, configuration).BuildAsync(tenantId, payload.ProductId, connectionId, cancellationToken);
                if (!current.Succeeded || !ShopifyProductPublicationComposer.SameVariantSkus(payload.PayloadJson, current.Value!.PayloadJson))
                    return await MarkPublicationResult(tenantId, connectionId, profile, "BLOCKED", "PRODUCT_PUBLICATION_SUPERSEDED", JobExecutionResult.Blocked("PRODUCT_PUBLICATION_SUPERSEDED", "Model ailesinin varyantları bu yayın hazırlandıktan sonra değişti; eski liste Shopify'a gönderilmedi. Kartı yeniden yayınlayın."), cancellationToken, payload.PayloadHash);
                // Every colour card's job carries the whole family product: the family is written and published once,
                // and a card whose family a sibling's job of the same round already sent reads its own SKUs from that send.
                if (await ShopifyFamilySend.CoveringSendAsync(db, tenantId, connectionId, job.Id, job.CreatedAt, payload, timeProvider.GetUtcNow(), cancellationToken) is { } familySend)
                {
                    job.PayloadJson = JsonSerializer.Serialize(payload with { Phase = "POLL", ExternalOperationId = familySend.ExternalOperationId, SubmittedAt = familySend.SubmittedAt });
                    job.PayloadHash = Hash(job.PayloadJson);
                    profile.ActualStatus = "SHOPIFY_SUBMITTED";
                    profile.LastRejectionCode = null;
                    profile.Version++;
                    await SetListingStatus(tenantId, connectionId, profile.Id, profile.ActualStatus, null, cancellationToken, payload.PayloadHash);
                    await db.SaveChangesAsync(cancellationToken);
                    return JobExecutionResult.Retry("PRODUCT_BATCH_PENDING", "Model ailesi Shopify'a tek ürün olarak gönderildi; bu kartın sonucu aynı gönderimden okunuyor.", TimeSpan.FromSeconds(2), familySend.ExternalOperationId);
                }
            }
            // A PTT AVM model product carries the whole family too, and UpdateProductsV3 deletes the variants it is not sent.
            var isPttAvmFamily = string.Equals(platformCode, "PTTAVM", StringComparison.OrdinalIgnoreCase) && PttAvmProductPayloadRules.IsVariantPayload(payload.PayloadJson);
            if (isPttAvmFamily)
            {
                var current = await new PttAvmProductPublicationComposer(db, configuration).BuildAsync(tenantId, payload.ProductId, connectionId, cancellationToken);
                if (!current.Succeeded || !PttAvmProductPayloadRules.SameVariantBarcodes(payload.PayloadJson, current.Value!.PayloadJson))
                    return await MarkPublicationResult(tenantId, connectionId, profile, "BLOCKED", "PRODUCT_PUBLICATION_SUPERSEDED", JobExecutionResult.Blocked("PRODUCT_PUBLICATION_SUPERSEDED", "Model ailesinin varyantları bu yayın hazırlandıktan sonra değişti; eski liste PttAVM'ye gönderilmedi. Kartı yeniden yayınlayın."), cancellationToken, payload.PayloadHash);
                // Every colour card's job carries the whole family: the family goes out once, and a card whose family a
                // sibling's job already sent follows that send's tracking result instead of sending it again.
                if (await PttAvmFamilySend.CoveringSendAsync(db, tenantId, connectionId, job.Id, job.CreatedAt, payload.PayloadJson, timeProvider.GetUtcNow(), cancellationToken) is { } familySend)
                {
                    job.PayloadJson = JsonSerializer.Serialize(payload with { Phase = "POLL", ExternalOperationId = familySend.ExternalOperationId, SubmittedAt = familySend.SubmittedAt });
                    job.PayloadHash = Hash(job.PayloadJson);
                    profile.ActualStatus = "BATCH_SUBMITTED";
                    profile.LastRejectionCode = null;
                    profile.Version++;
                    await SetListingStatus(tenantId, connectionId, profile.Id, profile.ActualStatus, null, cancellationToken, payload.PayloadHash);
                    await db.SaveChangesAsync(cancellationToken);
                    return JobExecutionResult.Retry("PRODUCT_BATCH_PENDING", "Model ailesi PttAVM'ye tek istekle gönderildi; bu kartın sonucu aynı gönderimden okunuyor.", TimeSpan.FromSeconds(15), familySend.ExternalOperationId);
                }
            }
            // A Shopify job whose earlier attempt wrote the product and stopped at its Online Store publication only
            // publishes it now: the product is never written or created again (ShopifyPublishOnlyFailure).
            var shopifyPublishOnlyResend = string.Equals(platformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase)
                && await ShopifyPublishOnlyFailure.EarlierAttemptWroteProductAsync(db, tenantId, job.Id, cancellationToken);
            // Any record stops the send (open, unresolved or done), except an open one a Shopify publish-only resend continues.
            var existingEffect = await effectGuard.FindAsync(tenantId, MarketplaceJobTypes.ProductCreate, job.EffectIdempotencyKey, cancellationToken);
            if (existingEffect is not null && !(shopifyPublishOnlyResend && existingEffect.CompletedAt is null)) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "EXTERNAL_EFFECT_AMBIGUOUS", JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS", "Önceki dış yazmanın sonucu kesinleştirilemedi; tekrar gönderim engellendi."), cancellationToken, payload.PayloadHash);

            var effect = existingEffect ?? effectGuard.New(tenantId, MarketplaceJobTypes.ProductCreate, job.EffectIdempotencyKey);
            var submitPayloadJson = shopifyPublishOnlyResend ? MarketplaceHub.Infrastructure.Adapters.Shopify.ShopifyHttpClient.MarkPublishOnly(payload.PayloadJson) : payload.PayloadJson;
            var send = await effectGuard.SendAsync(effect, () =>
            {
                TrackRequest();
                return products.CreateAsync(Context(tenantId, connectionId, correlationId, job.EffectIdempotencyKey), new ProductPublication(payload.ProductId, payload.PayloadHash, submitPayloadJson), cancellationToken);
            }, cancellationToken, saveBeforeSend: existingEffect is null);
            var submit = send.Result;
            if (!send.Accepted)
            {
                TrackResultFailure(submit.Error);
                var error = send.Error;
                // Shopify wrote the product and only its Online Store publication failed: the outcome is known, and a
                // retry or resend repeats only the publication.
                var shopifyPublishOnly = string.Equals(platformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase) && ShopifyFamilySend.IsPublishOnlyFailure(error.Code);
                if (!shopifyPublishOnly && send.Outcome == ExternalEffectSendOutcome.Uncertain)
                    return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "EXTERNAL_EFFECT_AMBIGUOUS", JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS", AmbiguousCreateSummary(error), error.RemoteRequestId), cancellationToken, payload.PayloadHash);
                await effectGuard.ReleaseAsync(effect, cancellationToken);
                // "Platformlarda Güncelle" found no product under the card's handle: it was deleted in Shopify. The card's links
                // are dropped and its publication reset, as a status check that finds none of its SKUs does, so the card can be
                // published again at once instead of reading live until the nightly verification.
                if (string.Equals(platformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase) && error.Code == MarketplaceHub.Infrastructure.Adapters.Shopify.ShopifyHttpClient.UpdateTargetMissingCode)
                {
                    var missingListings = await db.ChannelListingVariants.Where(x => x.TenantId == tenantId && x.ProfileId == profile.Id).ToListAsync(cancellationToken);
                    var missingVariantIds = missingListings.Select(x => x.VariantId).ToArray();
                    var missingStates = await db.MarketplaceListingStates.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && missingVariantIds.Contains(x.VariantId)).ToDictionaryAsync(x => x.VariantId, cancellationToken);
                    var reset = await ResetMissingPublication(tenantId, connectionId, profile, missingListings, missingStates, payload.PayloadHash, job.Id, cancellationToken);
                    if (reset.Kind == JobCompletionKind.Retry) return reset;
                    return JobExecutionResult.Blocked(error.Code, "Shopify'da bu kartın ürünü yok (Shopify'dan silinmiş); paneldeki Shopify bağları kaldırıldı. Ürünü Platformlarda Yayınla ile yeniden gönderebilirsiniz.");
                }
                // The variant send's dry run: the request was built and checked, nothing was written.
                if (isPttAvmFamily && error.Code == PttAvmProductPayloadRules.DryRunCode)
                    return await MarkPublicationResult(tenantId, connectionId, profile, "DRY_RUN", error.Code, JobExecutionResult.Blocked(error.Code, error.SafeMessage), cancellationToken, payload.PayloadHash);
                // PTT AVM already holds every variant of the family: nothing was sent and nothing went wrong.
                if (isPttAvmFamily && error.Code == PttAvmProductPayloadRules.VariantsAlreadyPresentCode)
                    return await MarkPublicationResult(tenantId, connectionId, profile, "ALREADY_PRESENT", error.Code, JobExecutionResult.SuccessWithNote(error.Code, error.SafeMessage), cancellationToken, payload.PayloadHash);
                var adapterResult = JobExecutionResult.FromAdapterError(error);
                // PTT AVM's five-minute wait for a repeated request holds this request only, not the connection's other jobs.
                if (error.Code == PttAvmProductPayloadRules.RepeatRequestWaitCode) adapterResult = adapterResult with { BackOffConnection = false };
                var status = adapterResult.Kind == JobCompletionKind.Retry ? "RETRY_SCHEDULED" : adapterResult.Kind == JobCompletionKind.ManualReview ? "MANUAL_REVIEW" : "BLOCKED";
                return await MarkPublicationResult(tenantId, connectionId, profile, status, error.Code, adapterResult, cancellationToken, payload.PayloadHash);
            }

            var operation = send.Value;
            var next = payload with { Phase = "POLL", ExternalOperationId = operation.ExternalOperationId, SubmittedAt = operation.SubmittedAt };
            job.PayloadJson = JsonSerializer.Serialize(next);
            job.PayloadHash = Hash(job.PayloadJson);
            profile.ActualStatus = string.Equals(platformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase)
                ? "IMPORT_SUBMITTED"
                : string.Equals(platformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase) ? "SHOPIFY_SUBMITTED" : "BATCH_SUBMITTED";
            profile.LastRejectionCode = null;
            profile.Version++;
            await SetListingStatus(tenantId, connectionId, profile.Id, profile.ActualStatus, null, cancellationToken, payload.PayloadHash);
            await db.SaveChangesAsync(cancellationToken);
            var waitingMessage = string.Equals(platformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase)
                ? "Hepsiburada ürün import sonucu bekleniyor."
                : string.Equals(platformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase) ? "Shopify ürün ve Online Store yayın sonucu kaydediliyor."
                : string.Equals(platformCode, "N11", StringComparison.OrdinalIgnoreCase) ? "N11 ürün açma görevinin sonucu bekleniyor." : string.Equals(platformCode, "PAZARAMA", StringComparison.OrdinalIgnoreCase) ? "Pazarama ürün açma sonucu bekleniyor." : string.Equals(platformCode, "PTTAVM", StringComparison.OrdinalIgnoreCase) ? "PttAVM ürün gönderimi sonucu bekleniyor." : "Trendyol create batch sonucu bekleniyor.";
            return JobExecutionResult.Retry("PRODUCT_BATCH_PENDING", waitingMessage, TimeSpan.FromSeconds(15), operation.ExternalOperationId);
        }

        if (!string.Equals(payload.Phase, "POLL", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(payload.ExternalOperationId) || payload.SubmittedAt is null) return JobExecutionResult.Blocked("PRODUCT_PUBLICATION_PHASE_INVALID", "Yayın işi bilinmeyen bir fazda.");
        if (timeProvider.GetUtcNow() - payload.SubmittedAt.Value > TimeSpan.FromHours(4)) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_BATCH_RESULT_EXPIRED", JobExecutionResult.ManualReview("PRODUCT_BATCH_RESULT_EXPIRED", "Batch sonucu dört saatlik sorgulama penceresinde tamamlanamadı.", payload.ExternalOperationId), cancellationToken, payload.PayloadHash);

        TrackRequest();
        var operationResult = await products.GetOperationAsync(Context(tenantId, connectionId, correlationId, $"{job.EffectIdempotencyKey}:poll"), payload.ExternalOperationId, cancellationToken);
        if (!operationResult.IsSuccess)
        {
            TrackResultFailure(operationResult.Error);
            var adapterResult = JobExecutionResult.FromAdapterError(operationResult.Error!);
            if (adapterResult.Kind == JobCompletionKind.Retry) return adapterResult;
            var status = adapterResult.Kind == JobCompletionKind.ManualReview ? "MANUAL_REVIEW" : "BLOCKED";
            return await MarkPublicationResult(tenantId, connectionId, profile, status, operationResult.Error!.Code, adapterResult, cancellationToken, payload.PayloadHash);
        }

        var operationStatus = operationResult.Value!;
        if (string.Equals(operationStatus.Status, "IN_PROGRESS", StringComparison.OrdinalIgnoreCase))
        {
            profile.ActualStatus = string.Equals(platformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase)
                ? "IMPORT_IN_PROGRESS"
                : string.Equals(platformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase) ? "SHOPIFY_IN_PROGRESS" : "BATCH_IN_PROGRESS";
            profile.Version++;
            await SetListingStatus(tenantId, connectionId, profile.Id, profile.ActualStatus, null, cancellationToken, payload.PayloadHash);
            await db.SaveChangesAsync(cancellationToken);
            var runningMessage = string.Equals(platformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase)
                ? "Hepsiburada ürün import işlemi sürüyor."
                : string.Equals(platformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase) ? "Shopify ürün yayın işlemi sürüyor."
                : string.Equals(platformCode, "N11", StringComparison.OrdinalIgnoreCase) ? "N11 ürün açma görevi işleniyor." : string.Equals(platformCode, "PAZARAMA", StringComparison.OrdinalIgnoreCase) ? "Pazarama ürün açma işlemi sürüyor." : string.Equals(platformCode, "PTTAVM", StringComparison.OrdinalIgnoreCase) ? "PttAVM ürün gönderimi işleniyor." : "Trendyol create batch işlemi sürüyor.";
            return JobExecutionResult.Retry("PRODUCT_BATCH_PENDING", runningMessage, TimeSpan.FromSeconds(15), payload.ExternalOperationId);
        }
        if (WholeBatchRejection(platformCode, operationStatus.Status) is { } refusal)
            return await MarkPublicationResult(tenantId, connectionId, profile, "CREATE_REJECTED", refusal.Code, JobExecutionResult.Blocked(refusal.Code, refusal.Summary, payload.ExternalOperationId), cancellationToken, payload.PayloadHash);
        if (!string.Equals(operationStatus.Status, "COMPLETED", StringComparison.OrdinalIgnoreCase)) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_BATCH_STATUS_UNKNOWN", JobExecutionResult.ManualReview("PRODUCT_BATCH_STATUS_UNKNOWN", "Batch servisi tanınmayan bir durum döndürdü.", payload.ExternalOperationId), cancellationToken, payload.PayloadHash);

        var listings = await db.ChannelListingVariants.Where(x => x.TenantId == tenantId && x.ProfileId == profile.Id).ToListAsync(cancellationToken);
        var listingVariantIds = listings.Select(x => x.VariantId).ToArray();
        var states = await db.MarketplaceListingStates.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && listingVariantIds.Contains(x.VariantId)).ToDictionaryAsync(x => x.VariantId, cancellationToken);
        // Only this job's variants answer: a resend of a card's refused variants leaves its live ones out of the result.
        listings = listings.Where(listing => !states.TryGetValue(listing.VariantId, out var sent) || string.Equals(sent.PayloadHash, payload.PayloadHash, StringComparison.Ordinal)).ToList();
        var isHepsiburada = string.Equals(platformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase);
        var isShopify = string.Equals(platformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase);
        // n11 task lines carry the stock code (the listing SKU) as their key.
        var isN11 = string.Equals(platformCode, "N11", StringComparison.OrdinalIgnoreCase);
        var keyedBySku = isHepsiburada || isShopify || isN11;
        var lines = operationStatus.Lines.Where(x => !string.IsNullOrWhiteSpace(x.ExternalKey)).ToList();
        var remoteKeyLabel = isN11 ? "stok kodu" : isShopify || isHepsiburada ? "merchantSku" : "barkod";
        var marketplaceName = isShopify ? "Shopify" : isHepsiburada ? "Hepsiburada" : isN11 ? "N11" : string.Equals(platformCode, "PAZARAMA", StringComparison.OrdinalIgnoreCase) ? "Pazarama" : string.Equals(platformCode, "PTTAVM", StringComparison.OrdinalIgnoreCase) ? "PttAVM" : "Trendyol";
        // No lines at all is a result not written yet: it waits below like any unlisted item, then the approval check
        // settles it (Shopify included, which used to go to review at once).
        if (lines.GroupBy(x => x.ExternalKey, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1)) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_BATCH_CONTRACT_INVALID", JobExecutionResult.ManualReview("PRODUCT_BATCH_CONTRACT_INVALID", $"Tamamlanan {marketplaceName} sonucu eksik veya yinelenen {remoteKeyLabel} satırları içeriyor.", payload.ExternalOperationId), cancellationToken, payload.PayloadHash);
        var resultKeys = listings.Select(x => keyedBySku ? x.ExternalSku ?? "" : x.ExternalBarcode ?? "").ToList();
        if (resultKeys.Any(string.IsNullOrWhiteSpace) || resultKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != listings.Count) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_BATCH_CONTRACT_INVALID", JobExecutionResult.ManualReview("PRODUCT_BATCH_CONTRACT_INVALID", $"Uzak sonuç eşlemesi için listing {remoteKeyLabel} değerleri eksik veya yinelenmiş.", payload.ExternalOperationId), cancellationToken, payload.PayloadHash);
        var resultKeyByListing = listings.ToDictionary(x => keyedBySku ? x.ExternalSku! : x.ExternalBarcode!, x => x, StringComparer.OrdinalIgnoreCase);
        var resultByKey = lines.ToDictionary(x => x.ExternalKey, StringComparer.OrdinalIgnoreCase);
        // A Shopify product, and a PTT AVM model product, carries the whole model family; this card's job records only
        // this card's SKUs/barcodes.
        var pttAvmParentBarcode = string.Equals(platformCode, "PTTAVM", StringComparison.OrdinalIgnoreCase) ? PttAvmProductPayloadRules.VariantParentBarcode(payload.PayloadJson) : null;
        if (pttAvmParentBarcode is not null) resultByKey = PttAvmProductPayloadRules.FamilyResultLines(pttAvmParentBarcode, resultByKey, resultKeyByListing.Keys);
        else if (isShopify) resultByKey = resultByKey.Where(x => resultKeyByListing.ContainsKey(x.Key)).ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        if (resultByKey.Keys.Any(key => !resultKeyByListing.ContainsKey(key))) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_BATCH_CONTRACT_INVALID", JobExecutionResult.ManualReview("PRODUCT_BATCH_CONTRACT_INVALID", "Batch sonucu bilinmeyen merchantSku/barkod içeriyor.", payload.ExternalOperationId), cancellationToken, payload.PayloadHash);
        // A sent item the result does not list yet, or lists without a final outcome (a refusal it marks retryable, as
        // Trendyol's not-yet SUCCESS/FAILED line), has no answer yet, on every marketplace.
        var missingKeys = resultKeyByListing.Keys
            .Where(key => !resultByKey.TryGetValue(key, out var listed) || (!listed.Succeeded && listed.Retryable))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // A result the marketplace has finished (UnlistedLinesUnconfirmed: Pazarama's done batch names only some codes)
        // will not name the rest later, so those go straight to the approval check instead of waiting.
        var elapsed = timeProvider.GetUtcNow() - payload.SubmittedAt.Value;
        if (!operationStatus.UnlistedLinesUnconfirmed && ProductBatchResultPolicy.KeepWaiting(missingKeys.Count, elapsed))
        {
            profile.ActualStatus = isHepsiburada ? "IMPORT_IN_PROGRESS" : isShopify ? "SHOPIFY_IN_PROGRESS" : "BATCH_IN_PROGRESS";
            profile.Version++;
            await SetListingStatus(tenantId, connectionId, profile.Id, profile.ActualStatus, null, cancellationToken, payload.PayloadHash);
            await db.SaveChangesAsync(cancellationToken);
            // Shopify's answer is kept with the job and does not change while it waits: one wait to the end of the grace.
            var wait = isShopify ? ProductBatchResultPolicy.IncompleteGrace - elapsed : TimeSpan.FromSeconds(15);
            return JobExecutionResult.Retry("PRODUCT_BATCH_PENDING", ProductBatchResultPolicy.WaitingMessage(marketplaceName, listings.Count - missingKeys.Count, listings.Count), wait < TimeSpan.FromSeconds(15) ? TimeSpan.FromSeconds(15) : wait, payload.ExternalOperationId);
        }

        // A barcode the marketplace refuses as one the store already holds (each marketplace in its own words) is read
        // by barcode in the approval check, as an unlisted one is: the product may be there, only the panel does not know it.
        var existingKeys = resultByKey.Where(x => !missingKeys.Contains(x.Key) && !x.Value.Succeeded && ProductBatchResultPolicy.SaysBarcodeExists(LineReason(x.Value))).Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var succeeded = 0;
        var unknown = 0;
        var missingReason = ProductBatchResultPolicy.MissingReason(marketplaceName);
        foreach (var listing in listings)
        {
            var key = keyedBySku ? listing.ExternalSku! : listing.ExternalBarcode!;
            if (missingKeys.Contains(key) || existingKeys.Contains(key))
            {
                // Unknown stays unknown: neither created nor refused; the approval check reads it by barcode.
                listing.ActualStatus = ProductBatchResultPolicy.ResultUnknownStatus;
                listing.RejectionCode = null;
                unknown++;
                if (states.TryGetValue(listing.VariantId, out var unknownState))
                {
                    unknownState.ActualStatus = listing.ActualStatus;
                    unknownState.LastRejectionCode = null;
                    unknownState.Version++;
                }
                continue;
            }
            var line = resultByKey[key];
            var rejection = line.Succeeded ? null : SafeCode(line.ErrorCode ?? "REMOTE_VALIDATION_FAILED");
            // Shopify writes and publishes the product in the same call and has no approval step: an accepted SKU is
            // live, as the card (profile) is. Leaving it CREATE_ACCEPTED counted none of the card's variants as live.
            listing.ActualStatus = line.Succeeded ? (isShopify ? "LIVE" : "CREATE_ACCEPTED") : "CREATE_REJECTED";
            listing.RejectionCode = rejection;
            if (line.Succeeded) succeeded++;
            if (line.Succeeded && isShopify) await EnsureLiveListingLink(tenantId, connectionId, listing, ShopifyVariantIdOrNull(line.ExternalId), createIfMissing: true, cancellationToken);
            if (states.TryGetValue(listing.VariantId, out var state))
            {
                state.ActualStatus = listing.ActualStatus;
                state.LastRejectionCode = rejection;
                state.Version++;
            }
        }
        var refused = listings.Count - succeeded - unknown;
        profile.ActualStatus = refused == 0 ? (isHepsiburada ? "IMPORT_ACCEPTED" : isShopify && unknown == 0 ? "LIVE" : "APPROVAL_PENDING") : succeeded + unknown == 0 ? "CREATE_REJECTED" : "PARTIAL_FAILURE";
        profile.LastRejectionCode = listings.Select(x => x.RejectionCode).FirstOrDefault(x => x is not null);
        profile.Version++;
        // Shopify has no approval step, but a SKU its answer left out is read by the same check (by SKU).
        if (isShopify ? unknown > 0 : succeeded + unknown > 0)
            await EnsureApprovalReconciliationJob(tenantId, connectionId, payload.ProductId, profile.Id, payload.PayloadHash, correlationId, cancellationToken, payload.ExternalOperationId,
                isShopify ? ProductApprovalReconciliationPolicy.ShopifyFirstCheckDelay : null);
        string? ListingReason(ChannelListingVariant listing)
        {
            var key = keyedBySku ? listing.ExternalSku! : listing.ExternalBarcode!;
            return missingKeys.Contains(key) ? missingReason : LineReason(resultByKey[key]);
        }
        await RecordJobVariantResults(tenantId, job.Id, MarketplaceJobTypes.ProductCreate, listings,
            listings.ToDictionary(x => x.VariantId, ListingReason), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        // Every variant is live, but the marketplace reported something that did not go through (Shopify: pictures); the
        // job ends with it so it shows instead of passing as a plain success.
        if (succeeded == listings.Count && operationStatus.IssueCode is { } issueCode)
            return JobExecutionResult.Blocked(issueCode, operationStatus.IssueSummary ?? issueCode, payload.ExternalOperationId);
        if (succeeded == listings.Count) return JobExecutionResult.Success();
        var incomplete = unknown > 0 ? " " + ProductBatchResultPolicy.IncompleteSummary(marketplaceName, unknown - existingKeys.Count, existingKeys.Count, listings.Count) : string.Empty;
        if (refused == 0) return JobExecutionResult.SuccessWithNote(ProductBatchResultPolicy.IncompleteResultCode, incomplete.Trim());
        // The marketplace's own reason per refused SKU (Trendyol failureReasons, n11's task line text), so it shows on the card.
        var reasons = RejectionReasons(listings
            .Where(listing => !missingKeys.Contains(keyedBySku ? listing.ExternalSku! : listing.ExternalBarcode!) && !existingKeys.Contains(keyedBySku ? listing.ExternalSku! : listing.ExternalBarcode!))
            .Select(listing => resultByKey[keyedBySku ? listing.ExternalSku! : listing.ExternalBarcode!]));
        return succeeded + unknown == 0
            ? JobExecutionResult.Blocked("PRODUCT_BATCH_REJECTED", $"{marketplaceName} ürün yayınındaki tüm varyantlar reddedildi.{reasons}", payload.ExternalOperationId)
            : JobExecutionResult.Blocked("PRODUCT_BATCH_PARTIAL_FAILURE", $"{marketplaceName} ürün yayınında {refused} varyant reddedildi.{reasons}{incomplete}", payload.ExternalOperationId);
    }

    // A create batch the marketplace refused as a whole, without a line per product: nothing counts as created.
    // n11 drops a rejected task without per-SKU lines, PTT AVM cancels the whole send, and Pazarama ends a batch in
    // error (status 3) without naming any product.
    internal static (string Code, string Summary)? WholeBatchRejection(string? platformCode, string operationStatus)
    {
        if (!string.Equals(operationStatus, "REJECTED", StringComparison.OrdinalIgnoreCase)) return null;
        return platformCode?.ToUpperInvariant() switch
        {
            "N11" => ("N11_TASK_REJECTED", "N11 ürün açma görevini reddetti; ürün açılmadı. Nedeni N11 satıcı panelindeki görev sonucunda görülebilir."),
            "PTTAVM" => ("PTTAVM_PRODUCT_REJECTED", "PttAVM ürün gönderimini iptal etti; ürün açılmadı. Nedeni PttAVM satıcı panelinde görülebilir."),
            "PAZARAMA" => ("PAZARAMA_BATCH_REJECTED", "Pazarama ürün açma partisini hata durumuyla bitirdi ve hiçbir ürün için sonuç vermedi; ürün açılmış sayılmadı. Nedeni Pazarama satıcı panelindeki parti sonucunda görülebilir."),
            _ => null
        };
    }

    private async Task<JobExecutionResult> ReconcileProductApproval(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        ProductApprovalReconciliationJobPayload? payload;
        try { payload = JsonSerializer.Deserialize<ProductApprovalReconciliationJobPayload>(payloadJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (JsonException) { return JobExecutionResult.Blocked("PRODUCT_APPROVAL_PAYLOAD_INVALID", "Ürün onay uzlaştırma işi payload sözleşmesini sağlamıyor."); }
        if (payload is null || payload.JobId == Guid.Empty || payload.ProductId == Guid.Empty || payload.ProfileId == Guid.Empty || string.IsNullOrWhiteSpace(payload.PayloadHash) || payload.DeadlineAt <= payload.StartedAt)
            return JobExecutionResult.Blocked("PRODUCT_APPROVAL_PAYLOAD_INVALID", "Ürün onay uzlaştırma işi zorunlu alanları eksik.");

        // Pazarama lists every product's approval state on two lists: one walk of them answers all of the connection's
        // waiting cards at once, instead of each card's job asking per barcode.
        var connectionPlatform = await ConnectionPlatformCode(tenantId, connectionId, cancellationToken);
        if (string.Equals(connectionPlatform, "PAZARAMA", StringComparison.OrdinalIgnoreCase) && products is IProductApprovalSnapshotPort snapshotPort)
            return await ReconcileConnectionApprovals(tenantId, connectionId, payload, correlationId, snapshotPort, cancellationToken);
        return await ReconcileProductApprovalCore(tenantId, connectionId, payload, correlationId, null, cancellationToken);
    }

    // One card's approval check. listed holds statuses already read from the marketplace's lists (Pazarama); a barcode
    // not in it is asked on its own, as before.
    private async Task<JobExecutionResult> ReconcileProductApprovalCore(Guid tenantId, Guid connectionId, ProductApprovalReconciliationJobPayload payload, string correlationId, IReadOnlyDictionary<string, RemotePublicationStatus>? listed, CancellationToken cancellationToken)
    {
        var job = await db.IntegrationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == payload.JobId && x.ConnectionId == connectionId && x.JobType == MarketplaceJobTypes.ProductApprovalReconcile, cancellationToken);
        var profile = await db.ChannelListingProfiles.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == payload.ProfileId && x.ProductId == payload.ProductId && x.ConnectionId == connectionId, cancellationToken);
        if (job is null || profile is null) return JobExecutionResult.Blocked("PRODUCT_APPROVAL_STATE_MISSING", "Onay uzlaştırma işi veya listing profile bulunamadı.");
        var platformCode = await ConnectionPlatformCode(tenantId, connectionId, cancellationToken);
        // n11 follows the same status-query flow, keyed by stock code (the listing SKU)
        // instead of barcode; an approved stock code becomes the variant's n11 link.
        var isN11 = string.Equals(platformCode, "N11", StringComparison.OrdinalIgnoreCase);
        var isPazarama = string.Equals(platformCode, "PAZARAMA", StringComparison.OrdinalIgnoreCase);
        var isPttAvm = string.Equals(platformCode, "PTTAVM", StringComparison.OrdinalIgnoreCase);
        var isShopify = string.Equals(platformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase);
        var marketplaceName = isN11 ? "N11" : isPazarama ? "Pazarama" : isPttAvm ? "PttAVM" : isShopify ? "Shopify" : "Trendyol";
        var keyLabel = isN11 || isShopify ? "stok kodu" : "barkod";
        Func<ChannelListingVariant, string?> approvalKey = item => isN11 || isShopify ? item.ExternalSku : item.ExternalBarcode;

        var listings = await db.ChannelListingVariants.Where(x => x.TenantId == tenantId && x.ProfileId == profile.Id).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        var allVariantIds = listings.Select(x => x.VariantId).ToArray();
        var states = allVariantIds.Length == 0
            ? new Dictionary<Guid, MarketplaceListingState>()
            : await db.MarketplaceListingStates.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && allVariantIds.Contains(x.VariantId)).ToDictionaryAsync(x => x.VariantId, cancellationToken);
        // A variant already live from an earlier publication keeps that payload's hash when only its refused siblings were
        // sent again; it does not make this check stale.
        if (states.Values.Any(x => !string.Equals(x.PayloadHash, payload.PayloadHash, StringComparison.Ordinal) && !string.Equals(x.ActualStatus, "LIVE", StringComparison.Ordinal)))
            return JobExecutionResult.Blocked("PRODUCT_APPROVAL_SUPERSEDED", "Daha yeni bir ürün yayınlama payload'ı bulundu; eski onay işi güncel listing durumunu değiştirmedi.");
        if (timeProvider.GetUtcNow() > payload.DeadlineAt)
            return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_APPROVAL_DEADLINE_EXPIRED", JobExecutionResult.ManualReview("PRODUCT_APPROVAL_DEADLINE_EXPIRED", ProductApprovalReconciliationPolicy.DeadlineExpiredMessage(marketplaceName, payload.StartedAt, payload.DeadlineAt)), cancellationToken);
        if (listings.Count == 0 || states.Count != listings.Count)
            return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_APPROVAL_STATE_INCOMPLETE", JobExecutionResult.ManualReview("PRODUCT_APPROVAL_STATE_INCOMPLETE", "Onay uzlaştırması için listing state kayıtları eksik."), cancellationToken);

        var candidates = listings.Where(x => !string.Equals(x.ActualStatus, "CREATE_REJECTED", StringComparison.Ordinal) && !string.Equals(x.ActualStatus, "UPDATE_REJECTED", StringComparison.Ordinal)).ToList();
        if (candidates.Count == 0 || candidates.Any(x => string.IsNullOrWhiteSpace(approvalKey(x))) || candidates.Select(approvalKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() != candidates.Count)
            return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_APPROVAL_BARCODES_INVALID", JobExecutionResult.ManualReview("PRODUCT_APPROVAL_BARCODES_INVALID", $"Onay uzlaştırması için kabul edilmiş ve benzersiz {keyLabel} listesi bulunamadı."), cancellationToken);

        if (string.Equals(platformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase))
            return await ReconcileHepsiburadaProductApproval(tenantId, connectionId, payload, profile, listings, states, correlationId, cancellationToken);
        if (isShopify)
            return await ReconcileShopifyPublication(tenantId, connectionId, payload, profile, listings, candidates, states, correlationId, cancellationToken);

        // Variants an earlier check of this publication already approved keep their recorded identity instead of being
        // read again; the adapter reads one barcode per request, so this is what keeps a long wait cheap.
        var candidateVariantIds = candidates.Select(x => x.VariantId).ToArray();
        // A Trendyol link made from the unapproved list holds the model code and barcodes, not Trendyol's ids: it settles
        // nothing and gives way to the ids the approval answers.
        var isTrendyol = string.Equals(platformCode, "TRENDYOL", StringComparison.OrdinalIgnoreCase);
        bool IsPlaceholder(string? externalId) => isTrendyol && ProductApprovalReconciliationPolicy.IsTrendyolPlaceholderId(externalId);
        var settledProductId = await db.MarketplaceProductLinks.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ProductId == payload.ProductId)
            .Select(x => x.ExternalId)
            .SingleOrDefaultAsync(cancellationToken);
        if (IsPlaceholder(settledProductId)) settledProductId = null;
        var settledVariantIds = settledProductId is null
            ? new Dictionary<Guid, string>()
            : (await db.MarketplaceVariantLinks.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && candidateVariantIds.Contains(x.VariantId))
                .ToListAsync(cancellationToken))
                .Where(x => !IsPlaceholder(x.ExternalId))
                .ToDictionary(x => x.VariantId, x => x.ExternalId);
        var remoteByBarcode = new Dictionary<string, RemotePublicationStatus>(StringComparer.OrdinalIgnoreCase);
        // A PTT AVM model family is one product under the parent barcode; its variant barcodes are not products of
        // their own, so the card's variants are read together through that product.
        Dictionary<string, RemotePublicationStatus>? familyStatuses = null;
        if (isPttAvm && products is IProductFamilyStatusPort familyStatusPort
            && await PttAvmPublicationFamilyAsync(tenantId, connectionId, payload.ProductId, payload.PayloadHash, cancellationToken) is ({ } sentFamily, var acceptedBarcodes))
        {
            var waiting = candidates
                .Where(listing => !ProductApprovalReconciliationPolicy.IsSettledLive(listing.ActualStatus, settledProductId is not null, settledVariantIds.ContainsKey(listing.VariantId)))
                .Select(listing => approvalKey(listing)!)
                .ToList();
            var parentBarcode = sentFamily.Barcode.Trim();
            // The colour and size each barcode was sent with: PTT AVM may list a variant only by that name.
            var sentVariants = (sentFamily.Variants ?? []).GroupBy(x => x.Barcode, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
            var queries = waiting.Select(barcode => sentVariants.TryGetValue(barcode, out var sent) ? new FamilyVariantQuery(barcode, sent.Color, sent.Size, acceptedBarcodes.Contains(barcode)) : new FamilyVariantQuery(barcode, null, null)).ToList();
            familyStatuses = new Dictionary<string, RemotePublicationStatus>(StringComparer.OrdinalIgnoreCase);
            if (waiting.Count > 0)
            {
                TrackRequest();
                var familyResult = await familyStatusPort.GetFamilyPublicationStatusAsync(Context(tenantId, connectionId, correlationId, $"product-approval:{profile.Id:N}:{parentBarcode}"), parentBarcode, queries, cancellationToken);
                if (!familyResult.IsSuccess)
                {
                    TrackResultFailure(familyResult.Error);
                    var adapterResult = JobExecutionResult.FromAdapterError(familyResult.Error!);
                    if (adapterResult.Kind == JobCompletionKind.Retry) return adapterResult;
                    return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", familyResult.Error!.Code, JobExecutionResult.ManualReview(familyResult.Error.Code, familyResult.Error.SafeMessage, familyResult.Error.RemoteRequestId), cancellationToken);
                }
                foreach (var status in familyResult.Value!) familyStatuses.TryAdd(status.Barcode, status);
            }
        }
        foreach (var listing in candidates)
        {
            var barcode = approvalKey(listing)!;
            if (ProductApprovalReconciliationPolicy.IsSettledLive(listing.ActualStatus, settledProductId is not null, settledVariantIds.TryGetValue(listing.VariantId, out var settledVariantId)))
            {
                remoteByBarcode.Add(barcode, new RemotePublicationStatus(barcode, "APPROVED", settledProductId, settledVariantId, null, "{}"));
                continue;
            }
            if (familyStatuses is not null)
            {
                if (!familyStatuses.TryGetValue(barcode, out var familyStatus))
                    return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_APPROVAL_CONTRACT_INVALID", JobExecutionResult.ManualReview("PRODUCT_APPROVAL_CONTRACT_INVALID", $"{marketplaceName} ürün durum yanıtı istenen {keyLabel} için sonuç içermiyor."), cancellationToken);
                remoteByBarcode.Add(barcode, familyStatus);
                continue;
            }
            if (listed is not null && listed.TryGetValue(barcode, out var listedStatus))
            {
                remoteByBarcode.Add(barcode, listedStatus with { Barcode = barcode });
                continue;
            }
            TrackRequest();
            var result = await products.GetPublicationStatusAsync(Context(tenantId, connectionId, correlationId, $"product-approval:{profile.Id:N}:{barcode}"), barcode, cancellationToken);
            if (!result.IsSuccess)
            {
                TrackResultFailure(result.Error);
                var adapterResult = JobExecutionResult.FromAdapterError(result.Error!);
                if (adapterResult.Kind == JobCompletionKind.Retry) return adapterResult;
                return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", result.Error!.Code, JobExecutionResult.ManualReview(result.Error.Code, result.Error.SafeMessage, result.Error.RemoteRequestId), cancellationToken);
            }
            var remoteStatus = result.Value!;
            if (!string.Equals(remoteStatus.Barcode, barcode, StringComparison.OrdinalIgnoreCase))
                return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_APPROVAL_CONTRACT_INVALID", JobExecutionResult.ManualReview("PRODUCT_APPROVAL_CONTRACT_INVALID", $"{marketplaceName} ürün durum yanıtı istenen {keyLabel} ile eşleşmedi."), cancellationToken);
            remoteByBarcode.Add(barcode, remoteStatus);
        }

        var localVariantIds = candidates.Select(x => x.VariantId).ToArray();
        // A just-created n11 product can be missing from the product query for a while, so a
        // missing n11 stock code stays pending instead of resetting the publication.
        if (!isN11 && !isPazarama && !isPttAvm && candidates.Count == listings.Count
            && ProductApprovalReconciliationPolicy.ShouldResetMissingPublication(listings.Count, remoteByBarcode.Values.ToArray(), await CreateConfirmedAsync(tenantId, connectionId, payload, cancellationToken)))
            return await ResetMissingPublication(tenantId, connectionId, profile, listings, states, payload.PayloadHash, payload.JobId, cancellationToken);

        var approvedStatuses = remoteByBarcode.Values.Where(x => x.Status == "APPROVED").ToList();
        if (approvedStatuses.Any(x => string.IsNullOrWhiteSpace(x.ExternalProductId) || string.IsNullOrWhiteSpace(x.ExternalVariantId)))
            return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_APPROVAL_IDENTIFIERS_MISSING", JobExecutionResult.ManualReview("PRODUCT_APPROVAL_IDENTIFIERS_MISSING", "Onaylanan ürün yanıtında contentId veya variantId bulunamadı."), cancellationToken);
        var approvedContentIds = approvedStatuses.Select(x => x.ExternalProductId!).Distinct(StringComparer.Ordinal).ToList();
        var hasSplitApprovedContents = ProductApprovalReconciliationPolicy.HasSplitApprovedContents(remoteByBarcode.Values);
        var approvedVariantIds = approvedStatuses.Select(x => x.ExternalVariantId!).ToList();
        if (approvedVariantIds.Distinct(StringComparer.Ordinal).Count() != approvedVariantIds.Count)
            return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_APPROVAL_VARIANT_ID_DUPLICATE", JobExecutionResult.ManualReview("PRODUCT_APPROVAL_VARIANT_ID_DUPLICATE", $"{marketplaceName} onay yanıtı birden fazla {keyLabel} için aynı variantId değerini döndürdü."), cancellationToken);
        // n11 groups a model's colour cards under one product (productMainId is the model code), so its id is held by the
        // family's first approved card; a sibling card is then not a conflict and goes live without a product link.
        // Pazarama does the same: the family's cards share one groupCode (from the model code), its product identity.
        var contentHeldByFamily = false;
        // The card's own Trendyol link when it is only the unapproved list's placeholder (model code): replaced below by
        // the content the approval answered, or dropped when that content is a sibling card's of the same model.
        MarketplaceProductLink? placeholderProductLink = null;
        if (approvedContentIds.Count > 0)
        {
            var localProductLink = await db.MarketplaceProductLinks.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ProductId == payload.ProductId, cancellationToken);
            if (localProductLink is not null && IsPlaceholder(localProductLink.ExternalId)) placeholderProductLink = localProductLink;
            var holders = await db.MarketplaceProductLinks.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && approvedContentIds.Contains(x.ExternalId) && x.ProductId != payload.ProductId)
                .Select(x => x.ProductId)
                .ToListAsync(cancellationToken);
            // Trendyol may file a colour sent again from the unapproved list into a content a sibling card of the model
            // already holds; that card keeps the content and this one goes live by its variants.
            if (holders.Count > 0 && (isN11 || isPazarama || placeholderProductLink is not null))
            {
                var family = await FamilyProductIdsAsync(tenantId, payload.ProductId, cancellationToken);
                contentHeldByFamily = holders.All(family.Contains);
            }
            // A PTT AVM model family is one product under the parent barcode: every colour card's variants answer with
            // that product's id, which the family's first approved card holds.
            if (holders.Count > 0 && isPttAvm)
                contentHeldByFamily = holders.All((await FamilyProductIdsAsync(tenantId, payload.ProductId, cancellationToken)).Contains);
            if (holders.Count > 0 && !contentHeldByFamily)
                return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_APPROVAL_IDENTITY_CONFLICT", JobExecutionResult.ManualReview("PRODUCT_APPROVAL_IDENTITY_CONFLICT", $"{marketplaceName} ürün kimliği başka bir yerel ürünle eşleşiyor."), cancellationToken);
            if (localProductLink is not null && placeholderProductLink is null && !approvedContentIds.Contains(localProductLink.ExternalId, StringComparer.Ordinal))
                return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_APPROVAL_IDENTITY_CONFLICT", JobExecutionResult.ManualReview("PRODUCT_APPROVAL_IDENTITY_CONFLICT", $"Yerel ürün daha önce farklı bir {marketplaceName} ürün kimliğiyle eşleştirilmiş."), cancellationToken);
        }
        if (approvedVariantIds.Count > 0 && await db.MarketplaceVariantLinks.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && approvedVariantIds.Contains(x.ExternalId) && !localVariantIds.Contains(x.VariantId), cancellationToken))
            return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_APPROVAL_IDENTITY_CONFLICT", JobExecutionResult.ManualReview("PRODUCT_APPROVAL_IDENTITY_CONFLICT", $"{marketplaceName} varyant kimliği başka bir yerel varyantla eşleşiyor."), cancellationToken);
        var approvedVariantByLocalId = candidates
            .Where(listing => remoteByBarcode[approvalKey(listing)!].Status == "APPROVED")
            .ToDictionary(listing => listing.VariantId, listing => remoteByBarcode[approvalKey(listing)!].ExternalVariantId!, EqualityComparer<Guid>.Default);
        var localVariantLinks = await db.MarketplaceVariantLinks.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && localVariantIds.Contains(x.VariantId)).ToListAsync(cancellationToken);
        if (localVariantLinks.Any(link => approvedVariantByLocalId.TryGetValue(link.VariantId, out var expectedExternalId) && !string.Equals(link.ExternalId, expectedExternalId, StringComparison.Ordinal) && !IsPlaceholder(link.ExternalId)))
            return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_APPROVAL_IDENTITY_CONFLICT", JobExecutionResult.ManualReview("PRODUCT_APPROVAL_IDENTITY_CONFLICT", $"Yerel varyant daha önce farklı bir {marketplaceName} varyant kimliğiyle eşleştirilmiş."), cancellationToken);

        var live = 0;
        var rejected = listings.Count - candidates.Count;
        var pending = 0;
        var exceptional = 0;
        var unconfirmedNotFound = 0;
        string? firstCode = listings.Where(x => string.Equals(x.ActualStatus, "CREATE_REJECTED", StringComparison.Ordinal) || string.Equals(x.ActualStatus, "UPDATE_REJECTED", StringComparison.Ordinal)).Select(x => x.RejectionCode).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (placeholderProductLink is not null)
            await ReplacePlaceholderProductLink(placeholderProductLink.Id, contentHeldByFamily ? null : approvedContentIds[0], cancellationToken);
        else if (approvedContentIds.Count == 1 && !contentHeldByFamily)
            await UpsertMarketplaceProductLink(tenantId, connectionId, payload.ProductId, approvedContentIds[0], cancellationToken);
        foreach (var listing in candidates)
        {
            var remote = remoteByBarcode[approvalKey(listing)!];
            // The adapters answer only these shared words: a state they do not recognise is UNKNOWN and a closed listing
            // ARCHIVED, both of which go to review here (never read as waiting or live).
            var localStatus = remote.Status switch
            {
                "APPROVED" => "LIVE",
                "PENDING_APPROVAL" => "APPROVAL_PENDING",
                // A barcode whose create was never confirmed stays unknown while not found, and goes to review after 24 h.
                "NOT_FOUND" => ProductApprovalReconciliationPolicy.NotFoundStatus(listing.ActualStatus, payload.StartedAt, timeProvider.GetUtcNow(), listing.RejectionCode),
                "REJECTED" => "REJECTED",
                "ARCHIVED" => "ARCHIVED",
                "LOCKED" => "LOCKED",
                "BLACKLISTED" => "BLACKLISTED",
                _ => "MANUAL_REVIEW"
            };
            var code = localStatus switch
            {
                "REJECTED" => SafeCode(remote.RejectionCode ?? "PRODUCT_APPROVAL_REJECTED"),
                "ARCHIVED" => "REMOTE_PRODUCT_ARCHIVED",
                "LOCKED" => "REMOTE_PRODUCT_LOCKED",
                "BLACKLISTED" => "REMOTE_PRODUCT_BLACKLISTED",
                "MANUAL_REVIEW" when remote.Status == "NOT_FOUND" => ProductApprovalReconciliationPolicy.UnconfirmedNotFoundCode,
                "MANUAL_REVIEW" => "PRODUCT_APPROVAL_STATUS_UNKNOWN",
                _ => null
            };
            listing.ActualStatus = localStatus;
            listing.DesiredStatus = "LIVE";
            listing.RejectionCode = code;
            if (states.TryGetValue(listing.VariantId, out var state))
            {
                state.DesiredStatus = "LIVE";
                state.ActualStatus = localStatus;
                state.LastRejectionCode = code;
                state.Version++;
            }

            switch (localStatus)
            {
                case "LIVE":
                    await UpsertMarketplaceVariantLink(tenantId, connectionId, listing.VariantId, remote.ExternalVariantId!, cancellationToken, replacePlaceholder: isTrendyol);
                    live++;
                    break;
                case "REJECTED": rejected++; firstCode ??= code; break;
                case "APPROVAL_PENDING" or ProductBatchResultPolicy.ResultUnknownStatus: pending++; break;
                default:
                    exceptional++; firstCode ??= code;
                    if (code == ProductApprovalReconciliationPolicy.UnconfirmedNotFoundCode) unconfirmedNotFound++;
                    break;
            }
        }

        profile.DesiredStatus = "LIVE";
        if (exceptional > 0)
        {
            profile.ActualStatus = "MANUAL_REVIEW";
            profile.LastRejectionCode = hasSplitApprovedContents ? ProductApprovalReconciliationPolicy.SplitApprovedContentsCode : firstCode;
        }
        else if (pending > 0)
        {
            profile.ActualStatus = live + rejected > 0 ? "APPROVAL_PARTIAL_PENDING" : "APPROVAL_PENDING";
            profile.LastRejectionCode = hasSplitApprovedContents ? ProductApprovalReconciliationPolicy.SplitApprovedContentsCode : firstCode;
        }
        else if (live == listings.Count)
        {
            profile.ActualStatus = "LIVE";
            profile.LastRejectionCode = hasSplitApprovedContents ? ProductApprovalReconciliationPolicy.SplitApprovedContentsCode : null;
            if (!hasSplitApprovedContents)
                await MarkProductLinkPublished(tenantId, connectionId, payload.ProductId, cancellationToken);
        }
        else if (hasSplitApprovedContents && live > 0)
        {
            profile.ActualStatus = "PARTIAL_LIVE";
            profile.LastRejectionCode = ProductApprovalReconciliationPolicy.SplitApprovedContentsCode;
        }
        else if (rejected == listings.Count)
        {
            profile.ActualStatus = "REJECTED";
            profile.LastRejectionCode = firstCode;
        }
        else
        {
            profile.ActualStatus = "PARTIAL_REJECTED";
            profile.LastRejectionCode = firstCode;
        }
        profile.Version++;
        await RecordJobVariantResults(tenantId, payload.JobId, MarketplaceJobTypes.ProductApprovalReconcile, listings,
            candidates.ToDictionary(x => x.VariantId, x => remoteByBarcode[approvalKey(x)!].RejectionCode), cancellationToken, payload.PayloadHash, states);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (ProductApprovalReconciliationPolicy.KeepsCheckingSiblings(exceptional, unconfirmedNotFound, pending))
            return JobExecutionResult.Retry("PRODUCT_APPROVAL_PENDING", $"{marketplaceName} ürün onayı kartın diğer varyantları için sürüyor; bulunamayan varyant incelemede.", ProductApprovalReconciliationPolicy.PendingRecheckDelay(payload.StartedAt, payload.DeadlineAt, timeProvider.GetUtcNow()));
        if (exceptional > 0) return JobExecutionResult.ManualReview(firstCode ?? "PRODUCT_APPROVAL_REVIEW_REQUIRED", $"{marketplaceName} ürün onayında operatör incelemesi gerektiren terminal durum oluştu.");
        if (pending > 0) return JobExecutionResult.Retry("PRODUCT_APPROVAL_PENDING", $"{marketplaceName} ürün onayı henüz tamamlanmadı.", ProductApprovalReconciliationPolicy.PendingRecheckDelay(payload.StartedAt, payload.DeadlineAt, timeProvider.GetUtcNow()));
        if (live == listings.Count) return JobExecutionResult.Success();
        return rejected == listings.Count
            ? JobExecutionResult.Blocked("PRODUCT_APPROVAL_REJECTED", $"{marketplaceName} ürün onayı tüm varyantlar için reddedildi.")
            : JobExecutionResult.Blocked("PRODUCT_APPROVAL_PARTIAL_REJECTION", $"{marketplaceName} ürün onayı bazı varyantlar için reddedildi.");
    }

    // How many of a connection's other waiting approval checks one run settles with the lists it read.
    internal const int ConnectionApprovalSiblingLimit = 200;

    // Pazarama keeps every product's approval state on its approved and unapproved lists. The approval check that runs
    // walks those lists once (ProductApprovalReconciliationPolicy.ListWalkBudget decides whether that is cheaper than
    // asking per barcode) and settles with them its own card and every other card of the connection still waiting for
    // approval, each through the same per-card rules (identity checks, the family's shared group code, the profile's
    // LIVE / partial / rejected roll-up and the card's own deadline). A card the lists did not show is asked on its own,
    // as before. Every settled card's job is completed or rescheduled on its own schedule with a check recorded, so the
    // youngest waiting card sets the pace and the others are read along with it instead of each calling Pazarama.
    // Jobs queued before this change carry the same per-card payload and are settled the same way.
    private async Task<JobExecutionResult> ReconcileConnectionApprovals(Guid tenantId, Guid connectionId, ProductApprovalReconciliationJobPayload payload, string correlationId, IProductApprovalSnapshotPort snapshotPort, CancellationToken cancellationToken)
    {
        var siblings = await db.IntegrationJobs
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.JobType == MarketplaceJobTypes.ProductApprovalReconcile && x.Id != payload.JobId
                && (x.Status == JobStatus.Pending || x.Status == JobStatus.RetryScheduled) && x.AttemptCount < x.MaxAttempts)
            .OrderBy(x => x.CreatedAt)
            .Take(ConnectionApprovalSiblingLimit)
            .ToListAsync(cancellationToken);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var waiting = new List<(IntegrationJob Job, ProductApprovalReconciliationJobPayload Payload)>();
        foreach (var sibling in siblings)
        {
            ProductApprovalReconciliationJobPayload? parsed = null;
            try { parsed = JsonSerializer.Deserialize<ProductApprovalReconciliationJobPayload>(sibling.PayloadJson, options); }
            catch (JsonException) { }
            // A card whose payload does not hold runs on its own and is refused there.
            if (parsed is null || parsed.JobId != sibling.Id || parsed.ProductId == Guid.Empty || parsed.ProfileId == Guid.Empty || string.IsNullOrWhiteSpace(parsed.PayloadHash) || parsed.DeadlineAt <= parsed.StartedAt) continue;
            if (parsed.ProfileId == payload.ProfileId) continue;
            waiting.Add((sibling, parsed));
        }

        var profileIds = waiting.Select(x => x.Payload.ProfileId).Append(payload.ProfileId).Distinct().ToArray();
        var waitingBarcodes = await db.ChannelListingVariants.AsNoTracking()
            .CountAsync(x => x.TenantId == tenantId && profileIds.Contains(x.ProfileId) && x.ActualStatus != "LIVE" && x.ActualStatus != "CREATE_REJECTED" && x.ActualStatus != "UPDATE_REJECTED", cancellationToken);
        var linkedVariants = await db.MarketplaceVariantLinks.AsNoTracking().CountAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId, cancellationToken);
        var unsettledListings = await db.MarketplaceListingStates.AsNoTracking().CountAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ActualStatus != "LIVE", cancellationToken);
        var budget = ProductApprovalReconciliationPolicy.ListWalkBudget(waitingBarcodes, linkedVariants, unsettledListings);
        if (budget == 0) return await ReconcileProductApprovalCore(tenantId, connectionId, payload, correlationId, null, cancellationToken);

        TrackRequest();
        var snapshot = await snapshotPort.ReadApprovalSnapshotAsync(Context(tenantId, connectionId, correlationId, $"product-approval-lists:{connectionId:N}"), budget, cancellationToken);
        if (!snapshot.IsSuccess)
        {
            TrackResultFailure(snapshot.Error);
            var adapterResult = JobExecutionResult.FromAdapterError(snapshot.Error!);
            // A quota or network refusal: this check waits like any other read. Anything else leaves this card to its
            // per-barcode lookup, which reports its own answer.
            if (adapterResult.Kind == JobCompletionKind.Retry) return adapterResult;
            return await ReconcileProductApprovalCore(tenantId, connectionId, payload, correlationId, null, cancellationToken);
        }

        var listed = snapshot.Value!.ByBarcode;
        var result = await ReconcileProductApprovalCore(tenantId, connectionId, payload, correlationId, listed, cancellationToken);
        if (result.Kind == JobCompletionKind.Retry && !JobWaitingPolicy.IsWaiting(result)) return result;
        foreach (var (sibling, siblingPayload) in waiting)
        {
            var settled = await ReconcileProductApprovalCore(tenantId, connectionId, siblingPayload, sibling.CorrelationId, listed, cancellationToken);
            // A per-barcode lookup the lists did not answer was refused for quota or network: the remaining cards run
            // on their own schedule instead.
            if (settled.Kind == JobCompletionKind.Retry && !JobWaitingPolicy.IsWaiting(settled)) break;
            await RecordSiblingApprovalCheck(sibling, settled, cancellationToken);
        }
        return result;
    }

    // Records on a card's own job the check another card's run made for it: one attempt, completed now, and the job
    // finished or planned for its next check, as the lease service would have done had the job run itself.
    private async Task RecordSiblingApprovalCheck(IntegrationJob job, JobExecutionResult result, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        job.AttemptCount++;
        if (JobWaitingPolicy.IsWaiting(result) && JobLeaseService.AllowanceAge(job, now) < JobWaitingPolicy.FreeWaitingWindow(job.JobType))
        {
            job.MaxAttempts++;
            job.WaitingChecks++;
        }
        var kind = result.Kind == JobCompletionKind.Retry && job.AttemptCount >= job.MaxAttempts ? JobCompletionKind.Dead : result.Kind;
        job.Status = kind switch
        {
            JobCompletionKind.Succeeded => JobStatus.Succeeded,
            JobCompletionKind.Retry => JobStatus.RetryScheduled,
            JobCompletionKind.Blocked => JobStatus.Blocked,
            JobCompletionKind.ManualReview => JobStatus.ManualReview,
            _ => JobStatus.Dead
        };
        job.StartedAt ??= now;
        job.CompletedAt = kind == JobCompletionKind.Retry ? null : now;
        if (kind == JobCompletionKind.Retry)
            job.AvailableAt = now.Add(result.RetryAfter is { } delay && delay > TimeSpan.Zero ? delay : TimeSpan.FromMinutes(5));
        job.LastErrorCode = kind == JobCompletionKind.Dead && result.Kind == JobCompletionKind.Retry ? result.ErrorCode ?? "MAX_ATTEMPTS_EXHAUSTED" : result.ErrorCode;
        job.LastErrorSummary = result.ErrorSummary;
        job.Version++;
        var attempt = new JobAttempt
        {
            Id = Guid.NewGuid(),
            TenantId = job.TenantId,
            JobId = job.Id,
            AttemptNumber = job.AttemptCount,
            StartedAt = now,
            CompletedAt = now,
            Succeeded = kind == JobCompletionKind.Succeeded,
            ErrorCode = job.LastErrorCode,
            ErrorSummary = result.ErrorSummary is null ? "Bağlantının bekleyen ürünleriyle birlikte tek liste okumasında kontrol edildi." : $"{result.ErrorSummary} Bağlantının bekleyen ürünleriyle birlikte tek liste okumasında kontrol edildi."
        };
        db.JobAttempts.Add(attempt);
        var outboxEvent = JobLeaseService.NewJobEvent(job, now);
        db.IntegrationOutboxEvents.Add(outboxEvent);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The job changed meanwhile (an operator cancelled or retried it): it keeps that change; the card's
            // listing state this check wrote stands, as it is what the marketplace answered.
            db.Entry(job).State = EntityState.Detached;
            db.Entry(attempt).State = EntityState.Detached;
            db.Entry(outboxEvent).State = EntityState.Detached;
        }
    }

    private async Task<JobExecutionResult> ReconcileHepsiburadaProductApproval(Guid tenantId, Guid connectionId, ProductApprovalReconciliationJobPayload payload, ChannelListingProfile profile, IReadOnlyList<ChannelListingVariant> listings, IReadOnlyDictionary<Guid, MarketplaceListingState> states, string correlationId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payload.ExternalOperationId))
            return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "HEPSIBURADA_TRACKING_ID_REQUIRED", JobExecutionResult.ManualReview("HEPSIBURADA_TRACKING_ID_REQUIRED", "Hepsiburada ürün durumunu uzlaştırmak için import trackingId bulunamadı."), cancellationToken);

        TrackRequest();
        var operation = await products.GetOperationAsync(Context(tenantId, connectionId, correlationId, $"hepsiburada-product-approval:{profile.Id:N}:{payload.ExternalOperationId}"), payload.ExternalOperationId, cancellationToken);
        if (!operation.IsSuccess)
        {
            TrackResultFailure(operation.Error);
            var mapped = JobExecutionResult.FromAdapterError(operation.Error!);
            return mapped.Kind == JobCompletionKind.Retry
                ? mapped
                : await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", operation.Error!.Code, JobExecutionResult.ManualReview(operation.Error.Code, operation.Error.SafeMessage, operation.Error.RemoteRequestId), cancellationToken);
        }
        if (string.Equals(operation.Value!.Status, "IN_PROGRESS", StringComparison.OrdinalIgnoreCase))
            return JobExecutionResult.Retry("HEPSIBURADA_PRODUCT_IMPORT_PENDING", "Hepsiburada ürün dosyası hâlâ işleniyor.", TimeSpan.FromMinutes(1), payload.ExternalOperationId);
        if (!string.Equals(operation.Value.Status, "COMPLETED", StringComparison.OrdinalIgnoreCase))
            return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "HEPSIBURADA_PRODUCT_IMPORT_STATUS_UNKNOWN", JobExecutionResult.ManualReview("HEPSIBURADA_PRODUCT_IMPORT_STATUS_UNKNOWN", "Hepsiburada ürün dosyası tanınmayan terminal durum döndürdü.", payload.ExternalOperationId), cancellationToken);

        // The trackingId answers the variants this file carried: a send of only a card's new sizes (PublicationUnsentVariants,
        // 5-11 Nov) leaves the sizes already on sale out of the file. Those keep the earlier payload's hash and stay as
        // they are (scan A-2, 13 Nov 2026: they fell to manual review with the whole card).
        var keptLive = listings.Count(listing => states.TryGetValue(listing.VariantId, out var held) && !string.Equals(held.PayloadHash, payload.PayloadHash, StringComparison.Ordinal));
        var allListings = listings;
        listings = listings.Where(listing => !states.TryGetValue(listing.VariantId, out var held) || string.Equals(held.PayloadHash, payload.PayloadHash, StringComparison.Ordinal)).ToList();
        var expectedSkus = listings.Select(x => x.ExternalSku ?? "").ToArray();
        var lines = operation.Value.Lines.Where(x => !string.IsNullOrWhiteSpace(x.ExternalKey)).ToList();
        if (listings.Count == 0
            || expectedSkus.Any(string.IsNullOrWhiteSpace)
            || expectedSkus.Distinct(StringComparer.OrdinalIgnoreCase).Count() != expectedSkus.Length
            || lines.Count != listings.Count
            || lines.GroupBy(x => x.ExternalKey, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "HEPSIBURADA_PRODUCT_STATUS_CONTRACT_INVALID", JobExecutionResult.ManualReview("HEPSIBURADA_PRODUCT_STATUS_CONTRACT_INVALID", "Hepsiburada trackingId satırları merchantSku üzerinden listing'lerle bire bir eşleşmiyor.", payload.ExternalOperationId), cancellationToken);
        var byMerchantSku = lines.ToDictionary(x => x.ExternalKey, StringComparer.OrdinalIgnoreCase);
        if (expectedSkus.Any(sku => !byMerchantSku.ContainsKey(sku)) || byMerchantSku.Keys.Any(sku => !expectedSkus.Contains(sku, StringComparer.OrdinalIgnoreCase)))
            return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "HEPSIBURADA_PRODUCT_STATUS_CONTRACT_INVALID", JobExecutionResult.ManualReview("HEPSIBURADA_PRODUCT_STATUS_CONTRACT_INVALID", "Hepsiburada trackingId sonucu eksik veya gönderilmemiş merchantSku içeriyor.", payload.ExternalOperationId), cancellationToken);

        var variantIds = listings.Select(x => x.VariantId).ToArray();
        var existingLinks = await db.MarketplaceVariantLinks.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && variantIds.Contains(x.VariantId)).ToDictionaryAsync(x => x.VariantId, cancellationToken);
        var live = 0;
        var rejected = 0;
        var pending = 0;
        var manualReview = 0;
        string? firstCode = null;
        foreach (var listing in listings)
        {
            var line = byMerchantSku[listing.ExternalSku!];
            var remoteStatus = line.Status?.ToUpperInvariant() ?? "";
            var localStatus = !line.Succeeded || remoteStatus is "MISSING_INFO" or "URUN_BILGILERI_EKSIK" or "REJECTED"
                ? "REJECTED"
                : remoteStatus is "MATCHED" or "SATISA_HAZIR" or "SALE_READY"
                    ? "LIVE"
                    : remoteStatus is "WAITING" or "INCELENECEK" or "GOREV_ACILMIS" or "ESLESEN" or "PRE_MATCHED" or "MATCHED_WITH_STAGED" or "ON_KATALOG_ESLESEN" or "CREATED" or "KATALOG_SURECINDE" or "APPROVAL_PENDING" or "WAITING_APPROVAL"
                        ? "APPROVAL_PENDING"
                        : "MANUAL_REVIEW";
            var rejection = localStatus switch
            {
                "REJECTED" => SafeCode(line.ErrorCode ?? (remoteStatus == "MISSING_INFO" ? "PRODUCT_MISSING_INFO" : "PRODUCT_IMPORT_REJECTED")),
                "MANUAL_REVIEW" => "HEPSIBURADA_PRODUCT_STATUS_UNKNOWN",
                _ => null
            };
            if (localStatus == "LIVE")
            {
                if (string.IsNullOrWhiteSpace(line.ExternalId)
                    || await db.MarketplaceVariantLinks.AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalId == line.ExternalId && x.VariantId != listing.VariantId, cancellationToken)
                    || (existingLinks.TryGetValue(listing.VariantId, out var priorLink) && !string.Equals(priorLink.ExternalId, line.ExternalId, StringComparison.Ordinal)))
                {
                    localStatus = "MANUAL_REVIEW";
                    rejection = "HEPSIBURADA_PRODUCT_IDENTITY_CONFLICT";
                }
                else
                    await UpsertMarketplaceVariantLink(tenantId, connectionId, listing.VariantId, line.ExternalId, cancellationToken);
            }
            listing.ActualStatus = localStatus;
            listing.DesiredStatus = "LIVE";
            listing.RejectionCode = rejection;
            if (states.TryGetValue(listing.VariantId, out var state))
            {
                // An update of a variant on sale that waits in review or was refused leaves it selling what it had.
                state.ActualStatus = ListingSaleStatePolicy.AfterImportResult(state.ActualStatus, localStatus);
                state.DesiredStatus = "LIVE";
                state.LastRejectionCode = rejection;
                state.Version++;
            }
            switch (localStatus)
            {
                case "LIVE": live++; break;
                case "REJECTED": rejected++; firstCode ??= rejection; break;
                case "APPROVAL_PENDING": pending++; break;
                default: manualReview++; firstCode ??= rejection; break;
            }
        }

        // The sizes left out of this file were already on sale (the stale-payload check above lets only LIVE ones by).
        live += keptLive;
        profile.DesiredStatus = "LIVE";
        profile.LastRejectionCode = firstCode;
        profile.ActualStatus = manualReview > 0 ? "MANUAL_REVIEW"
            : pending > 0 ? (live + rejected > 0 ? "APPROVAL_PARTIAL_PENDING" : "APPROVAL_PENDING")
            : live == allListings.Count ? "LIVE"
            : rejected == allListings.Count ? "REJECTED"
            : "PARTIAL_REJECTED";
        profile.Version++;
        await RecordJobVariantResults(tenantId, payload.JobId, MarketplaceJobTypes.ProductApprovalReconcile, listings,
            listings.ToDictionary(x => x.VariantId, x => LineReason(byMerchantSku[x.ExternalSku!])), cancellationToken, payload.PayloadHash, states);
        await db.SaveChangesAsync(cancellationToken);
        if (manualReview > 0) return JobExecutionResult.ManualReview(firstCode ?? "HEPSIBURADA_PRODUCT_STATUS_UNKNOWN", "Hepsiburada ürün durum eşlemesi veya HB SKU kimliği belirsiz; manuel inceleme gerekir.", payload.ExternalOperationId);
        if (pending > 0) return JobExecutionResult.Retry("HEPSIBURADA_PRODUCT_APPROVAL_PENDING", "Hepsiburada ürün inceleme/eşleşme süreci henüz tamamlanmadı.", TimeSpan.FromMinutes(5), payload.ExternalOperationId);
        if (rejected > 0) return JobExecutionResult.Blocked(rejected == allListings.Count ? "HEPSIBURADA_PRODUCT_REJECTED" : "HEPSIBURADA_PRODUCT_PARTIAL_REJECTION", "Hepsiburada ürün import sonucu eksik bilgi veya ret içeriyor.", payload.ExternalOperationId);
        return JobExecutionResult.Success();
    }

    private async Task<JobExecutionResult> MarkApprovalResult(Guid tenantId, Guid connectionId, ChannelListingProfile profile, string status, string? rejectionCode, JobExecutionResult result, CancellationToken cancellationToken)
    {
        profile.ActualStatus = status;
        profile.LastRejectionCode = SafeCode(rejectionCode);
        profile.Version++;
        var listings = await db.ChannelListingVariants.Where(x => x.TenantId == tenantId && x.ProfileId == profile.Id).ToListAsync(cancellationToken);
        var candidates = listings.Where(x => !string.Equals(x.ActualStatus, "CREATE_REJECTED", StringComparison.Ordinal) && !string.Equals(x.ActualStatus, "UPDATE_REJECTED", StringComparison.Ordinal)).ToList();
        var variantIds = candidates.Select(x => x.VariantId).ToArray();
        var states = variantIds.Length == 0
            ? new Dictionary<Guid, MarketplaceListingState>()
            : await db.MarketplaceListingStates.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && variantIds.Contains(x.VariantId)).ToDictionaryAsync(x => x.VariantId, cancellationToken);
        foreach (var listing in candidates)
        {
            // A variant already on sale (approved by an earlier check, or live before a send of only its new siblings)
            // stays on sale: the check could not settle the card, it did not see that variant leave sale.
            states.TryGetValue(listing.VariantId, out var state);
            var keepsLive = state is not null && ListingSaleStatePolicy.AfterUnsettledApproval(state.ActualStatus, status, rejectionCode) == ListingSaleStatePolicy.Live;
            if (keepsLive) { state!.LastRejectionCode = SafeCode(rejectionCode); state.Version++; continue; }
            listing.ActualStatus = status;
            listing.RejectionCode = SafeCode(rejectionCode);
            if (state is not null)
            {
                state.ActualStatus = status;
                state.LastRejectionCode = SafeCode(rejectionCode);
                state.Version++;
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        return result;
    }

    // Shopify answers each SKU at once. Every SKU missing from the store resets the card to unpublished; every SKU on
    // an active product makes it live. A draft product or a SKU not shown yet waits for the next check; anything else
    // (a read that fails, an archived product, an unknown status) leaves the state for a person to look at instead of guessing. A model family shares one Shopify product, so no product
    // link is written here: the store's product is not this card's alone.
    private async Task<JobExecutionResult> ReconcileShopifyPublication(
        Guid tenantId,
        Guid connectionId,
        ProductApprovalReconciliationJobPayload payload,
        ChannelListingProfile profile,
        IReadOnlyList<ChannelListingVariant> listings,
        IReadOnlyList<ChannelListingVariant> candidates,
        IReadOnlyDictionary<Guid, MarketplaceListingState> states,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var remote = new List<RemotePublicationStatus>(candidates.Count);
        foreach (var listing in candidates)
        {
            var sku = listing.ExternalSku!;
            TrackRequest();
            var result = await products.GetPublicationStatusAsync(Context(tenantId, connectionId, correlationId, $"product-approval:{profile.Id:N}:{sku}"), sku, cancellationToken);
            if (!result.IsSuccess)
            {
                TrackResultFailure(result.Error);
                var adapterResult = JobExecutionResult.FromAdapterError(result.Error!);
                if (adapterResult.Kind == JobCompletionKind.Retry) return adapterResult;
                // The product is in Shopify but Shopify refused its Online Store publication: a known outcome with
                // Shopify's own reason, not something to guess about.
                if (ShopifyFamilySend.IsPublishOnlyFailure(result.Error!.Code))
                    return await MarkApprovalResult(tenantId, connectionId, profile, "BLOCKED", result.Error.Code, JobExecutionResult.Blocked(result.Error.Code, result.Error.SafeMessage, result.Error.RemoteRequestId), cancellationToken);
                return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", result.Error!.Code, JobExecutionResult.ManualReview(result.Error.Code, result.Error.SafeMessage, result.Error.RemoteRequestId), cancellationToken);
            }
            remote.Add(result.Value!);
        }

        if (candidates.Count == listings.Count
            && ProductApprovalReconciliationPolicy.ShouldResetMissingShopifyPublication(listings.Count, remote, await CreateConfirmedAsync(tenantId, connectionId, payload, cancellationToken), payload.StartedAt, timeProvider.GetUtcNow()))
            return await ResetMissingPublication(tenantId, connectionId, profile, listings, states, payload.PayloadHash, payload.JobId, cancellationToken);
        // A draft product or a SKU Shopify does not show yet waits like a pending approval elsewhere (until the
        // deadline); an archived product or a status the adapter does not know goes to review.
        if (remote.Count == candidates.Count
            && remote.All(status => status.Status is "APPROVED" or "PENDING_APPROVAL" or "NOT_FOUND")
            && remote.Any(status => status.Status is "PENDING_APPROVAL" or "NOT_FOUND"))
            return JobExecutionResult.Retry("PRODUCT_APPROVAL_PENDING", "Shopify ürünü henüz etkin değil veya stok kodu henüz görünmüyor.", ProductApprovalReconciliationPolicy.PendingRecheckDelay(payload.StartedAt, payload.DeadlineAt, timeProvider.GetUtcNow()));
        if (remote.Count != candidates.Count || remote.Any(status => !string.Equals(status.Status, "APPROVED", StringComparison.Ordinal)))
        {
            var missing = remote.Where(status => !string.Equals(status.Status, "APPROVED", StringComparison.Ordinal)).Select(status => $"{status.Barcode} ({status.Status})");
            return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "SHOPIFY_PUBLICATION_PARTIAL", JobExecutionResult.ManualReview("SHOPIFY_PUBLICATION_PARTIAL", $"Shopify'da bu kartın varyantlarının hepsi etkin bir üründe değil: {string.Join(", ", missing)}."), cancellationToken);
        }

        foreach (var (listing, index) in candidates.Select((listing, index) => (listing, index)))
        {
            listing.ActualStatus = "LIVE";
            listing.RejectionCode = null;
            if (states.TryGetValue(listing.VariantId, out var state))
            {
                state.ActualStatus = "LIVE";
                state.LastRejectionCode = null;
                state.Version++;
            }
            // The variant link (not the product link): Shopify answered this SKU's own variant id.
            await EnsureLiveListingLink(tenantId, connectionId, listing, ShopifyVariantIdOrNull(remote[index].ExternalVariantId), createIfMissing: true, cancellationToken);
        }
        profile.ActualStatus = candidates.Count == listings.Count ? "LIVE" : "PARTIAL_FAILURE";
        profile.LastRejectionCode = null;
        profile.Version++;
        await RecordJobVariantResults(tenantId, payload.JobId, MarketplaceJobTypes.ProductApprovalReconcile, listings, null, cancellationToken, payload.PayloadHash, states);
        await db.SaveChangesAsync(cancellationToken);
        return JobExecutionResult.Success();
    }

    // Whether the marketplace's batch result confirmed any barcode of this payload as there (its create job recorded it
    // SENT, or Trendyol refused it as a barcode the store already holds); see
    // ProductApprovalReconciliationPolicy.ShouldResetMissingPublication.
    private async Task<bool> CreateConfirmedAsync(Guid tenantId, Guid connectionId, ProductApprovalReconciliationJobPayload payload, CancellationToken cancellationToken)
    {
        var createDedup = $"product-create:{connectionId:N}:{payload.ProductId:N}:{payload.PayloadHash}";
        return await (from job in db.IntegrationJobs.AsNoTracking()
                      join result in db.JobVariantResults.AsNoTracking() on new { job.TenantId, JobId = job.Id } equals new { result.TenantId, result.JobId }
                      where job.TenantId == tenantId && job.ConnectionId == connectionId && job.JobType == MarketplaceJobTypes.ProductCreate
                          && job.JobDedupKey == createDedup
                          && (result.Status == JobVariantResultPolicy.Sent
                              || (result.Reason != null && result.Reason.Contains(ProductBatchResultPolicy.BarcodeExistsMarker)))
                      select result.Id).AnyAsync(cancellationToken);
    }

    // The waiting approval checks of a publication Trendyol confirmed missing are closed. J6 (15 Nov 2026): they were closed
    // without a "jobs" event, so an İşlemler screen open in another tab kept showing them queued; one bulk event for all of
    // them (K2) is saved with the reset's own changes.
    internal static int CloseApprovalChecksOfMissingPublication(AppDbContext db, Guid tenantId, IEnumerable<IntegrationJob> relatedJobs, Guid currentJobId, DateTimeOffset now)
    {
        var closed = 0;
        foreach (var oldCheck in relatedJobs.Where(job => job.JobType == MarketplaceJobTypes.ProductApprovalReconcile && job.Id != currentJobId
            && job.Status is JobStatus.Pending or JobStatus.RetryScheduled))
        {
            Jobs.JobStateMachine.Cancel(db, oldCheck, now, "PRODUCT_MISSING_RESET", "Trendyol ilanının bulunmadığı doğrulandığı için eski durum kontrolü kapatıldı.", Jobs.JobEvent.None);
            closed++;
        }
        Jobs.JobStateMachine.PublishBulk(db, tenantId, now, JobStatus.Cancelled, closed);
        return closed;
    }

    private async Task<JobExecutionResult> ResetMissingPublication(
        Guid tenantId,
        Guid connectionId,
        ChannelListingProfile profile,
        IReadOnlyCollection<ChannelListingVariant> listings,
        IReadOnlyDictionary<Guid, MarketplaceListingState> states,
        string payloadHash,
        Guid currentJobId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var productCreatePrefix = $"product-create:{connectionId:N}:{profile.ProductId:N}:";
        var productUpdatePrefix = $"product-update:{connectionId:N}:{profile.ProductId:N}:";
        var productArchivePrefix = $"product-archive:{connectionId:N}:{profile.ProductId:N}:";
        var approvalDedup = $"product-approval:{connectionId:N}:{profile.Id:N}:{payloadHash}";
        var approvalManualPrefix = $"{approvalDedup}:manual-v";
        var productCreateDedup = $"{productCreatePrefix}{payloadHash}";
        var relatedJobs = await db.IntegrationJobs.FromSqlInterpolated($"""
            SELECT * FROM integration.jobs
            WHERE "TenantId" = {tenantId} AND "ConnectionId" = {connectionId}
              AND (("JobType" = {MarketplaceJobTypes.ProductCreate} AND "JobDedupKey" LIKE {productCreatePrefix + "%"})
                OR ("JobType" = {MarketplaceJobTypes.ProductUpdate} AND "JobDedupKey" LIKE {productUpdatePrefix + "%"})
                OR ("JobType" = {MarketplaceJobTypes.ProductArchive} AND "JobDedupKey" LIKE {productArchivePrefix + "%"})
                OR ("JobType" = {MarketplaceJobTypes.ProductApprovalReconcile}
                    AND ("JobDedupKey" = {approvalDedup} OR "JobDedupKey" LIKE {approvalManualPrefix + "%"})))
            FOR UPDATE
            """).ToListAsync(cancellationToken);
        if (relatedJobs.Any(job => job.Id != currentJobId && job.JobType != MarketplaceJobTypes.ProductApprovalReconcile
            && job.Status is JobStatus.Pending or JobStatus.Leased or JobStatus.RetryScheduled))
            return JobExecutionResult.Retry("PRODUCT_WRITE_IN_PROGRESS", "Ürün için başka bir yayın işlemi sürüyor; durum kontrolü yeniden denenecek.", TimeSpan.FromMinutes(1));
        if (relatedJobs.Any(job => job.JobType == MarketplaceJobTypes.ProductApprovalReconcile
            && job.Id != currentJobId && job.Status == JobStatus.Leased))
            return JobExecutionResult.Retry("PRODUCT_APPROVAL_CHECK_IN_PROGRESS", "Başka bir durum kontrolü sürüyor; ürünün bulunmadığı doğrulaması yeniden denenecek.", TimeSpan.FromMinutes(1));

        var now = timeProvider.GetUtcNow();
        CloseApprovalChecksOfMissingPublication(db, tenantId, relatedJobs, currentJobId, now);
        var previousCreate = relatedJobs.SingleOrDefault(job => job.JobType == MarketplaceJobTypes.ProductCreate && job.JobDedupKey == productCreateDedup);
        if (previousCreate is not null)
        {
            previousCreate.JobDedupKey = $"{previousCreate.JobDedupKey}:not-found:{previousCreate.Id:N}";
            previousCreate.Version++;
        }
        var previousApproval = relatedJobs.SingleOrDefault(job => job.JobType == MarketplaceJobTypes.ProductApprovalReconcile && job.JobDedupKey == approvalDedup);
        if (previousApproval is not null)
        {
            previousApproval.JobDedupKey = $"{previousApproval.JobDedupKey}:not-found:{previousApproval.Id:N}";
            previousApproval.Version++;
        }

        profile.DesiredStatus = "DRAFT";
        profile.ActualStatus = "UNKNOWN";
        profile.LastRejectionCode = null;
        profile.Version++;
        foreach (var listing in listings)
        {
            listing.DesiredStatus = "DRAFT";
            listing.ActualStatus = "UNKNOWN";
            listing.RejectionCode = null;
            if (states.TryGetValue(listing.VariantId, out var state))
            {
                state.DesiredStatus = "DRAFT";
                state.ActualStatus = "UNKNOWN";
                state.LastRejectionCode = null;
                state.PayloadHash = null;
                state.Version++;
            }
        }

        var productLink = await db.MarketplaceProductLinks.SingleOrDefaultAsync(
            x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ProductId == profile.ProductId,
            cancellationToken);
        if (productLink is not null) db.MarketplaceProductLinks.Remove(productLink);
        var variantIds = listings.Select(x => x.VariantId).ToArray();
        var variantLinks = await db.MarketplaceVariantLinks.Where(
            x => x.TenantId == tenantId && x.ConnectionId == connectionId && variantIds.Contains(x.VariantId))
            .ToListAsync(cancellationToken);
        db.MarketplaceVariantLinks.RemoveRange(variantLinks);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return JobExecutionResult.Success();
    }

    private async Task EnsureApprovalReconciliationJob(Guid tenantId, Guid connectionId, Guid productId, Guid profileId, string payloadHash, string correlationId, CancellationToken cancellationToken, string? externalOperationId = null, TimeSpan? firstCheckDelay = null)
    {
        var dedup = $"product-approval:{connectionId:N}:{profileId:N}:{payloadHash}";
        var now = timeProvider.GetUtcNow();
        var jobId = Guid.CreateVersion7();
        var payload = JsonSerializer.Serialize(new ProductApprovalReconciliationJobPayload(jobId, productId, profileId, payloadHash, now, now.AddDays(7), externalOperationId));
        // J4 (KUYRUK-8): a check with this key in any state is the check.
        await Jobs.JobEnqueuer.EnqueueAsync(db, new IntegrationJobRequest(tenantId, connectionId, MarketplaceJobTypes.ProductApprovalReconcile, payload, dedup, correlationId, now + (firstCheckDelay ?? TimeSpan.Zero))
        {
            Id = jobId,
            Priority = JobPriorityPolicy.For(MarketplaceJobTypes.ProductApprovalReconcile, userInitiated: true),
            MaxAttempts = ProductApprovalReconcileMaxAttempts,
            CreatedAt = now
        }, JobDedupPolicy.ReuseAnyWithKey, cancellationToken);
    }

    // " Reddedilen: SKU (reason); …" for the refused lines that carry a reason, cut to stay readable; empty without one.
    internal static string RejectionReasons(IEnumerable<RemoteOperationLine> lines)
    {
        // Trendyol gives its reasons as failureReasons (Reasons); n11 and Pazarama write theirs as the line's status text.
        var parts = lines
            .Where(line => !line.Succeeded)
            .Select(line => (line.ExternalKey, Reason: line.Reasons is { Count: > 0 } reasons ? JobVariantResultPolicy.Reason(reasons) : JobVariantResultPolicy.Reason([line.Status])))
            .Where(item => item.Reason is not null)
            .Select(item => $"{item.ExternalKey} ({item.Reason})")
            .ToList();
        if (parts.Count == 0) return string.Empty;
        var text = " Reddedilen: " + string.Join("; ", parts);
        return text.Length <= 900 ? text : text[..897] + "…";
    }

    // The colour cards of the product's model family (same brand and model code), the product itself included.
    // The family request a PTT AVM publication went out as: the request of this publication's product job (the same
    // publication hash), with its parent barcode and each variant's colour and size. Null for a product sent without
    // variants, or when no such job is found.
    // AcceptedBarcodes: the variant barcodes a succeeded product job of this publication sent (its tracking result accepted them).
    private async Task<(PttAvmProductPayloadRules.ProductDraft? Family, HashSet<string> AcceptedBarcodes)> PttAvmPublicationFamilyAsync(Guid tenantId, Guid connectionId, Guid productId, string publicationHash, CancellationToken cancellationToken)
    {
        var jobs = await db.IntegrationJobs.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.JobType == MarketplaceJobTypes.ProductCreate && x.PayloadJson.Contains(publicationHash))
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new { x.PayloadJson, x.Status })
            .Take(50)
            .ToListAsync(cancellationToken);
        var payloads = jobs.Select(x => x.PayloadJson).ToList();
        return (PttAvmProductPayloadRules.PublicationFamily(payloads, productId, publicationHash),
            PttAvmProductPayloadRules.AcceptedFamilyBarcodes(jobs.Where(x => x.Status == JobStatus.Succeeded).Select(x => x.PayloadJson), publicationHash));
    }

    private async Task<List<Guid>> FamilyProductIdsAsync(Guid tenantId, Guid productId, CancellationToken cancellationToken)
    {
        var card = await EffectiveCatalog.Products(db).Where(x => x.TenantId == tenantId && x.Id == productId).Select(x => new { x.BrandId, x.FamilyId }).SingleOrDefaultAsync(cancellationToken);
        var brandId = card?.BrandId;
        var modelCode = await db.ProductVariants.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ProductId == productId && x.ModelCode != null)
            .OrderBy(x => x.SortOrder).ThenBy(x => x.Id)
            .Select(x => x.ModelCode)
            .FirstOrDefaultAsync(cancellationToken);
        var ids = await ProductFamilyScope.ProductIdsAsync(db, tenantId, brandId, modelCode, db.ProductVariants.AsNoTracking(), cancellationToken, card?.FamilyId);
        if (!ids.Contains(productId)) ids.Add(productId);
        return ids;
    }
}
