using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Hepsiburada;
using MarketplaceHub.Infrastructure.Adapters.Pazarama;
using MarketplaceHub.Infrastructure.Adapters.PttAvm;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

// ISLEYICI-9 stage 5 step 3c (16 Nov 2026): the price and stock writes (PRICE_INVENTORY_SYNC with its poll), the automatic
// stock dispatch (STOCK_PROJECTION_DISPATCH) and the stock catch-up (STOCK_RECONCILIATION), moved out of the job processor
// as they were, with the link health a price/stock answer records. Every send opens, sends and closes its ExternalEffect
// record through the processor's ExternalEffectGuard and checks the same write switches (JobWriteSwitches). Automatic
// price and stock writes are off on every live connection by the owner's choice; nothing here changes a switch or its
// default. It works for one run of one job (run) on the job's AppDbContext.
internal sealed class PriceStockService(
    AppDbContext db,
    IssueRecorder issues,
    IProductPort products,
    IInventoryPricePort inventoryPrice,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ExternalEffectGuard effectGuard,
    JobWriteSwitches writeSwitches,
    OrderIngestionService orderIngestion,
    ListingVerificationService listingVerification,
    JobRunContext run)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // What the moved code used from the processor: the same classes and helpers the processor calls.
    private Task RecordIssue(Guid tenantId, string key, string code, string summary, CancellationToken cancellationToken) =>
        issues.RecordAsync(tenantId, key, code, summary, timeProvider.GetUtcNow(), cancellationToken);

    // An offer the automatic send leaves out (PriceInventoryOfferIssues). One the owner marked "Sorun Yok" on the values the
    // send refuses now stays resolved and is only seen again, so the retention keeps the mark (OfferIssueNoProblem).
    private async Task RecordOfferIssue(Guid tenantId, Guid connectionId, Guid variantId, string code, string summary, CancellationToken cancellationToken)
    {
        var key = PriceInventoryOfferIssues.Key(connectionId, variantId);
        var issue = db.OperationalIssues.Local.FirstOrDefault(x => x.TenantId == tenantId && x.DedupeKey == key)
            ?? await db.OperationalIssues.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.DedupeKey == key, cancellationToken);
        if (issue is not null && await OfferIssueNoProblem.StillMarkedAsync(db, issue, connectionId, variantId, code, cancellationToken))
        {
            issue.LastSeenAt = timeProvider.GetUtcNow();
            return;
        }
        await RecordIssue(tenantId, key, code, summary, cancellationToken);
    }

    private AdapterContext Context(Guid tenantId, Guid connectionId, string correlationId, string idempotency) => new(tenantId, connectionId, correlationId, idempotency, timeProvider.GetUtcNow().AddMinutes(2));

    private void TrackRequest() => run.TrackRequest();

    private void TrackReceived() => run.TrackReceived();

    private void TrackResultFailure(AdapterError? error) => run.TrackResultFailure(error);

    private static string Hash(string value) => TextValues.Sha256Hex(value);

    private static string Short(string? value, int maximum) => CatalogAttributeService.Short(value, maximum);

    private static string? SafeCode(string? value) => ProductJobService.SafeCode(value);

    private static string MarketplacePlatformName(string? platformCode) => VariantLinkBookkeeping.MarketplacePlatformName(platformCode);

    private Task<string?> ConnectionPlatformCode(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        orderIngestion.ConnectionPlatformCode(tenantId, connectionId, cancellationToken);

    private Task QueueListingLookupsForRefusedCodes(Guid tenantId, Guid connectionId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken) =>
        listingVerification.QueueListingLookupsForRefusedCodes(tenantId, connectionId, variantIds, cancellationToken);

    private bool WritesEnabled(string settingsJson) => writeSwitches.WritesEnabled(settingsJson);

    private Task<bool> PttAvmManualWritesAllowedAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        writeSwitches.PttAvmManualWritesAllowedAsync(tenantId, connectionId, cancellationToken);

    private Task<bool> ExternalWritePolicyEnabledAsync(Guid tenantId, Guid connectionId, string resourceType, CancellationToken cancellationToken) =>
        writeSwitches.ExternalWritePolicyEnabledAsync(tenantId, connectionId, resourceType, cancellationToken);

    private Task<bool> StrictWritePolicyEnabledAsync(Guid tenantId, Guid connectionId, string resourceType, CancellationToken cancellationToken) =>
        writeSwitches.StrictWritePolicyEnabledAsync(tenantId, connectionId, resourceType, cancellationToken);

    private Task<int> ExternalWriteDelaySecondsAsync(Guid tenantId, Guid connectionId, string resourceType, CancellationToken cancellationToken) =>
        writeSwitches.ExternalWriteDelaySecondsAsync(tenantId, connectionId, resourceType, cancellationToken);

    internal async Task<JobExecutionResult> SyncPriceInventory(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        PriceInventoryJobPayload? payload;
        try { payload = JsonSerializer.Deserialize<PriceInventoryJobPayload>(payloadJson, JsonOptions); }
        catch (JsonException) { return JobExecutionResult.Blocked("PRICE_INVENTORY_PAYLOAD_INVALID", "Fiyat-stok işi payload sözleşmesini sağlamıyor."); }
        if (payload is null || payload.JobId == Guid.Empty || payload.ConnectionId != connectionId || string.IsNullOrWhiteSpace(payload.PayloadHash) || string.IsNullOrWhiteSpace(payload.PayloadJson)) return JobExecutionResult.Blocked("PRICE_INVENTORY_PAYLOAD_INVALID", "Fiyat-stok işi zorunlu alanları eksik.");
        var job = await db.IntegrationJobs.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == payload.JobId && x.ConnectionId == connectionId && x.JobType == MarketplaceJobTypes.PriceInventorySync, cancellationToken);
        if (job is null) return JobExecutionResult.Blocked("PRICE_INVENTORY_STATE_MISSING", "Fiyat-stok işi bulunamadı.");
        var phase = payload.Phase.Trim().ToUpperInvariant();
        if (phase == "SUBMIT")
        {
            var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId, cancellationToken);
            if (connection is null) return JobExecutionResult.Blocked("CONNECTION_NOT_FOUND", "Bağlantı bulunamadı.");
            // The SIT inventory upload of a STAGE Hepsiburada connection is a manual Stage operation: it is
            // not gated by the write switches (the adapter allows it only as a manual operational Stage
            // write with PRICE_WRITE/INVENTORY_WRITE evidence) and it is built from the imported offers.
            var hepsiburadaStageManual = HepsiburadaStageCertificationPolicy.IsManualStagePriceInventory(connection, payload.Manual);
            if (!hepsiburadaStageManual && !WritesEnabled(connection.SettingsJson))
                return JobExecutionResult.Blocked("EXTERNAL_WRITES_DISABLED", "Dış yazma kapalı olduğu için fiyat-stok gönderimi çalıştırılmadı.");
            // Every platform's request carries price and stock together (Trendyol's too), so an
            // automatic send needs both policies; a manual send is guarded by the connection switch alone.
            foreach (var writePolicy in PriceInventoryOutboxPolicy.RequiredWritePolicies(payload.Manual, connection.PlatformCode))
            {
                if (writePolicy == MarketplaceExternalWritePolicies.Manual)
                {
                    if (!await PttAvmManualWritesAllowedAsync(tenantId, connectionId, cancellationToken))
                        return JobExecutionResult.Blocked(PttAvmManualWritePolicy.DisabledCode, PttAvmManualWritePolicy.DisabledMessage);
                    continue;
                }
                // An automatic send needs a policy someone switched on; a missing row is off (owner decision A).
                var policyOpen = payload.Automatic
                    ? await StrictWritePolicyEnabledAsync(tenantId, connectionId, writePolicy, cancellationToken)
                    : await ExternalWritePolicyEnabledAsync(tenantId, connectionId, writePolicy, cancellationToken);
                if (!policyOpen)
                    return JobExecutionResult.Blocked("EXTERNAL_WRITE_POLICY_DISABLED", writePolicy == MarketplaceExternalWritePolicies.Stock ? "Stok dış yazma akışı kapalı; pazar yeri isteği gönderilmedi." : "Fiyat dış yazma akışı kapalı; pazar yeri isteği gönderilmedi.");
            }
            var current = hepsiburadaStageManual
                ? await new PriceInventoryComposer(db).BuildHepsiburadaStageAsync(tenantId, connectionId, cancellationToken, payload.VariantId)
                : await new PriceInventoryComposer(db).BuildAsync(tenantId, connectionId, cancellationToken, payload.VariantId, payload.ProductId, payload.Automatic, payload.HoldPrice, payload.HoldStock);
            if (!current.Succeeded)
            {
                if (current.Error!.Code == "NO_EXTERNAL_CHANGES") return JobExecutionResult.Success();
                if (current.Error.Code == "INVENTORY_REQUIRED")
                {
                    await RecordIssue(tenantId, $"price-inventory-inventory-required:{connectionId:N}", current.Error.Code, current.Error.Message, cancellationToken);
                    await db.SaveChangesAsync(cancellationToken);
                }
                return JobExecutionResult.Blocked(current.Error.Code, current.Error.Message);
            }
            // Offers without a MAIN stock row are left out of the request; they stay visible as an issue.
            if (current.Value!.MissingInventorySkus.Count > 0)
                await RecordIssue(tenantId, $"price-inventory-inventory-required:{connectionId:N}", "INVENTORY_REQUIRED", PriceInventoryComposer.MissingInventoryMessage(current.Value.MissingInventorySkus), cancellationToken);
            if (!string.Equals(current.Value!.PayloadHash, payload.PayloadHash, StringComparison.Ordinal)) return JobExecutionResult.Blocked("PRICE_INVENTORY_SUPERSEDED", "Fiyat veya stok yeni bir sürüme geçti; eski payload uzak sisteme gönderilmedi.");
            // Price and stock are sent as absolute values (Shopify sets stock against the quantity it reads
            // first), and the payload is the current one (checked above). A send whose outcome is unknown,
            // for example cut off by a deploy, is therefore sent again instead of waiting for review.
            // Unlike every other write, any record (open, unresolved or done) is opened again and the values sent again.
            var effect = await effectGuard.FindAsync(tenantId, MarketplaceJobTypes.PriceInventorySync, job.EffectIdempotencyKey, cancellationToken);
            if (effect is null) effect = effectGuard.New(tenantId, MarketplaceJobTypes.PriceInventorySync, job.EffectIdempotencyKey);
            // A record left by an earlier job of the same values (one that gave up its key) is opened again.
            else { effect.CompletedAt = null; effect.Outcome = null; }
            var send = await effectGuard.SendAsync(effect, () =>
            {
                TrackRequest();
                return inventoryPrice.PushPriceAndInventoryAsync(Context(tenantId, connectionId, correlationId, job.EffectIdempotencyKey) with { Operation = hepsiburadaStageManual ? IntegrationOperation.Manual : IntegrationOperation.Automatic }, payload.PayloadJson, cancellationToken);
            }, cancellationToken);
            var submit = send.Result;
            if (!send.Accepted)
            {
                TrackResultFailure(submit.Error);
                // An unclear answer resends the same absolute values instead of waiting for review; the record stays open
                // until then. PTT AVM refuses the same request again within five minutes, so its resend waits past that window.
                if (send.Outcome == ExternalEffectSendOutcome.Uncertain) return JobExecutionResult.Retry("PRICE_INVENTORY_OUTCOME_UNKNOWN", "Fiyat-stok çağrısının sonucu kesinleşmedi; aynı değerler yeniden gönderilecek.", connection.PlatformCode == "PTTAVM" ? TimeSpan.FromMinutes(6) : TimeSpan.FromSeconds(30), submit.Error!.RemoteRequestId);
                effectGuard.Release(effect);
                // A refused request is logged once the refusal is final; one to be sent again is logged by the send that follows.
                var refusal = JobExecutionResult.FromAdapterError(submit.Error!);
                if (!IsRepeatRequestRefusal(submit.Error!) && refusal.Kind != JobCompletionKind.Retry)
                    await PriceStockSendLogRecorder.AddAsync(db, tenantId, connectionId, connection.PlatformCode, job, payload, submit.Error!.RemoteRequestId, timeProvider.GetUtcNow(), PriceStockSendResults.Failed, submit.Error.Code, submit.Error.SafeMessage, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                // Trendyol (15 minutes) and PTT AVM (five) refuse a request identical to one they took a moment ago. When
                // every value equals the one the marketplace last confirmed, it already holds them; otherwise the same
                // request goes again once the window has passed (the earlier one's outcome is not known here).
                if (IsRepeatRequestRefusal(submit.Error!))
                {
                    if (await SettleRepeatedPriceInventoryAsync(tenantId, connectionId, payload.Lines, cancellationToken))
                    {
                        const string heldNote = "Pazar yeri aynı isteği kısa süre önce almıştı; değerler son onaylanan değerlerle aynı olduğu için gönderilmiş sayıldı.";
                        await PriceStockSendLogRecorder.AddAsync(db, tenantId, connectionId, connection.PlatformCode, job, payload, submit.Error!.RemoteRequestId, timeProvider.GetUtcNow(), PriceStockSendResults.Unchanged, submit.Error.Code, heldNote, cancellationToken);
                        await db.SaveChangesAsync(cancellationToken);
                        return JobExecutionResult.SuccessWithNote(submit.Error!.Code, heldNote);
                    }
                    return refusal with { BackOffConnection = false };
                }
                return refusal;
            }
            var op = send.Value;
            var next = payload with { Phase = "POLL", ExternalOperationId = op.ExternalOperationId, SubmittedAt = op.SubmittedAt };
            job.PayloadJson = JsonSerializer.Serialize(next); job.PayloadHash = Hash(job.PayloadJson);
            await PriceStockSendLogRecorder.AddAsync(db, tenantId, connectionId, connection.PlatformCode, job, payload, op.ExternalOperationId, timeProvider.GetUtcNow(), PriceStockSendResults.Submitted, null, null, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return JobExecutionResult.Retry("PRICE_INVENTORY_BATCH_PENDING", "Fiyat-stok batch sonucu bekleniyor.", TimeSpan.FromSeconds(15), op.ExternalOperationId);
        }
        if (phase != "POLL" || string.IsNullOrWhiteSpace(payload.ExternalOperationId) || payload.SubmittedAt is null) return JobExecutionResult.Blocked("PRICE_INVENTORY_PHASE_INVALID", "Fiyat-stok işi bilinmeyen bir fazda.");
        var platformCode = await ConnectionPlatformCode(tenantId, connectionId, cancellationToken);
        // Pazarama may answer a batch up to four hours late, so it is waited for eight; the others for four.
        var pollDeadline = PriceInventorySendPolicy.PollDeadline(platformCode);
        if (timeProvider.GetUtcNow() - payload.SubmittedAt.Value > pollDeadline) return await SettleSendLogAsync(tenantId, job.Id, payload, PriceStockSendResults.Unknown, JobExecutionResult.ManualReview("PRICE_INVENTORY_BATCH_EXPIRED", $"Fiyat-stok batch sonucu {pollDeadline.TotalHours:0} saatlik pencerede alınamadı.", payload.ExternalOperationId), cancellationToken);
        TrackRequest();
        var operation = await products.GetOperationAsync(Context(tenantId, connectionId, correlationId, $"{job.EffectIdempotencyKey}:poll"), payload.ExternalOperationId, cancellationToken);
        if (!operation.IsSuccess)
        {
            TrackResultFailure(operation.Error);
            var readFailure = JobExecutionResult.FromAdapterError(operation.Error!);
            return readFailure.Kind == JobCompletionKind.Retry ? readFailure : await SettleSendLogAsync(tenantId, job.Id, payload, PriceStockSendResults.Unknown, readFailure, cancellationToken);
        }
        TrackReceived();
        if (operation.Value!.Status.Equals("IN_PROGRESS", StringComparison.OrdinalIgnoreCase)) return JobExecutionResult.Retry("PRICE_INVENTORY_BATCH_PENDING", "Fiyat-stok batch sonucu bekleniyor.", TimeSpan.FromSeconds(20), payload.ExternalOperationId);
        if (operation.Value.Status.Equals("REJECTED", StringComparison.OrdinalIgnoreCase)) return await SettleSendLogAsync(tenantId, job.Id, payload, PriceStockSendResults.Rejected, JobExecutionResult.Blocked("PRICE_INVENTORY_REJECTED", "Pazar yeri fiyat-stok isteğini satır sonucu vermeden tamamen reddetti; fiyat ve stok değerlerini kontrol edin.", payload.ExternalOperationId), cancellationToken);
        if (!operation.Value.Status.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase)) return await SettleSendLogAsync(tenantId, job.Id, payload, PriceStockSendResults.Unknown, JobExecutionResult.ManualReview("PRICE_INVENTORY_BATCH_STATUS_UNKNOWN", "Fiyat-stok batch servisi tanınmayan durum döndürdü.", payload.ExternalOperationId), cancellationToken);
        var isHepsiburada = string.Equals(platformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase);
        var isPttAvm = string.Equals(platformCode, "PTTAVM", StringComparison.OrdinalIgnoreCase);
        var isTrendyol = string.Equals(platformCode, "TRENDYOL", StringComparison.OrdinalIgnoreCase);
        // Hepsiburada takes the adapter's inventory-upload cap (1000; 4000 was never confirmed). PTT AVM lines are the variants
        // sent: up to 1000 products of up to 100 variants each.
        var maximumLineCount = isHepsiburada ? HepsiburadaHttpClient.MaxInventoryUploadItems : isPttAvm ? PttAvmPriceInventoryRules.MaxItemsPerRequest * PttAvmPriceInventoryRules.MaxVariantsPerProduct : string.Equals(platformCode, "PAZARAMA", StringComparison.OrdinalIgnoreCase) ? PazaramaPriceInventoryRules.MaxItemsPerRequest : 1000;
        var duplicateKeys = isHepsiburada
            ? payload.Lines.Select(x => x.MerchantSku).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            : payload.Lines.Select(x => x.Barcode).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        if (payload.Lines.Count == 0 || payload.Lines.Count > maximumLineCount || duplicateKeys != payload.Lines.Count)
            return await SettleSendLogAsync(tenantId, job.Id, payload, PriceStockSendResults.Unknown, JobExecutionResult.ManualReview("PRICE_INVENTORY_PAYLOAD_INVALID", "Fiyat-stok job satırları eksik, yinelenen veya limit dışı."), cancellationToken);
        var offerIds = payload.Lines.Select(x => x.OfferId).ToArray();
        var offers = await db.ChannelOffers.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && offerIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        if (offers.Count != payload.Lines.Count) return await SettleSendLogAsync(tenantId, job.Id, payload, PriceStockSendResults.Unknown, JobExecutionResult.ManualReview("PRICE_INVENTORY_STATE_INCOMPLETE", "Fiyat-stok uzlaştırmasında teklif kayıtları eksik."), cancellationToken);
        var variantIds = payload.Lines.Select(x => x.VariantId).Distinct().ToArray();
        var inventory = await db.InventoryItems.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.LocationCode == "MAIN" && variantIds.Contains(x.VariantId))
            .ToDictionaryAsync(x => x.VariantId, cancellationToken);
        if (inventory.Count != variantIds.Length) return await SettleSendLogAsync(tenantId, job.Id, payload, PriceStockSendResults.Unknown, JobExecutionResult.ManualReview("PRICE_INVENTORY_STATE_INCOMPLETE", "Fiyat-stok uzlaştırmasında MAIN stok projection kayıtları eksik."), cancellationToken);
        // Each line is judged on its own: a line whose price or stock changed while the batch was polled keeps the
        // marketplace's answer for what it sent (LastSent*), while only the marks still naming its versions move on; the
        // other lines are recorded as usual. Before, one changed line threw away the answer of the whole batch.
        bool StockCurrent(PriceInventoryPushLine line) => PriceInventorySendPolicy.StockCurrent(line, inventory[line.VariantId].ProjectionVersion, offers[line.OfferId].SafetyStock);
        bool PriceCurrent(PriceInventoryPushLine line) => PriceInventorySendPolicy.PriceCurrent(line, offers[line.OfferId].PriceVersion);
        var lineByBarcode = operation.Value.Lines.Where(x => !string.IsNullOrWhiteSpace(x.ExternalKey)).GroupBy(x => x.ExternalKey, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);
        if (isHepsiburada && operation.Value.Lines.Any(remoteLine => !payload.Lines.Any(localLine =>
                string.Equals(localLine.MerchantSku, remoteLine.ExternalKey, StringComparison.OrdinalIgnoreCase)
                || string.Equals(localLine.HepsiburadaSku, remoteLine.ExternalKey, StringComparison.OrdinalIgnoreCase))))
            return await SettleSendLogAsync(tenantId, job.Id, payload, PriceStockSendResults.Unknown, JobExecutionResult.ManualReview("PRICE_INVENTORY_BATCH_CONTRACT_INVALID", "Hepsiburada fiyat-stok sonucu gönderilmeyen bir merchantSku/hbSku içeriyor.", payload.ExternalOperationId), cancellationToken);
        // The request's log rows, settled line by line below and saved with the offers.
        var sendLog = await PriceStockSendLogRecorder.PendingAsync(db, tenantId, job.Id, payload.ExternalOperationId, timeProvider.GetUtcNow(), cancellationToken);
        if (string.Equals(platformCode, "PAZARAMA", StringComparison.OrdinalIgnoreCase))
            return await RecordPazaramaPriceInventory(tenantId, connectionId, job.Id, payload, offers, lineByBarcode, sendLog, StockCurrent, PriceCurrent, cancellationToken);
        var success = 0; var failed = 0; var superseded = 0; var uncertain = 0; var notSent = 0;
        var unknownCodes = new Dictionary<Guid, string>();
        var confirmedCodes = new List<Guid>();
        foreach (var line in payload.Lines)
        {
            var found = isHepsiburada
                ? line.MerchantSku is not null && lineByBarcode.TryGetValue(line.MerchantSku, out var remoteLineByMerchantSku)
                    ? (Found: true, Remote: remoteLineByMerchantSku)
                    : line.HepsiburadaSku is not null && lineByBarcode.TryGetValue(line.HepsiburadaSku, out var remoteLineByHbSku)
                        ? (Found: true, Remote: remoteLineByHbSku)
                        : (Found: false, Remote: (RemoteOperationLine?)null)
                : lineByBarcode.TryGetValue(isPttAvm ? line.ExternalVariantId ?? line.Barcode : line.Barcode, out var remoteLineByBarcode)
                    ? (Found: true, Remote: remoteLineByBarcode)
                    : (Found: false, Remote: (RemoteOperationLine?)null);
            // A line the answer does not name (Trendyol, n11, PTT AVM; Pazarama is judged below in its own way) is
            // neither applied nor refused: it stays uncertain (UNKNOWN), is not recorded as sent, so the same values go
            // again with the next send, and the job asks for a look. The lines the answer names are recorded as usual.
            // Only Hepsiburada lists the refused lines alone, so there a line left out was applied (its API says so).
            if (!found.Found && !isHepsiburada)
            {
                uncertain++;
                var missingCode = isPttAvm ? "PTTAVM_TRACKING_LINE_MISSING" : "PRICE_INVENTORY_LINE_UNCONFIRMED";
                var missingKey = isPttAvm ? line.ExternalVariantId ?? line.Barcode : line.Barcode;
                var missing = $"Pazar yeri fiyat-stok sonucu {missingKey} için satır vermedi; {line.Barcode} için sonuç belirsiz, gönderilmiş sayılmadı.";
                await RecordIssue(tenantId, $"price-inventory:{connectionId}:{missingKey}:{missingCode}", missingCode, missing, cancellationToken);
                sendLog.Settle(line, PriceStockSendResults.Unknown, missingCode, missing);
                continue;
            }
            // A Hepsiburada upload that failed as a whole proves nothing of the lines it leaves out: they are not recorded
            // as sent, so the same values go again with the next send.
            if (!found.Found && operation.Value.UnlistedLinesUnconfirmed)
            {
                failed++;
                var unconfirmed = $"Pazar yeri fiyat-stok yüklemesi başarısız döndü ve {line.Barcode} için satır sonucu vermedi; gönderilmiş sayılmadı.";
                await RecordIssue(tenantId, $"price-inventory:{connectionId}:{line.Barcode}:PRICE_INVENTORY_UPLOAD_FAILED", "PRICE_INVENTORY_UPLOAD_FAILED", unconfirmed, cancellationToken);
                sendLog.Settle(line, PriceStockSendResults.Failed, "PRICE_INVENTORY_UPLOAD_FAILED", unconfirmed);
                continue;
            }
            var offer = offers[line.OfferId];
            // ADAPTOR-DIGER-1: a send cut off part-way names the lines it never sent (NOT_SENT): failed, not refused, no
            // issue, the offer's marks stay, so the same values go with the next send.
            if (found.Found && found.Remote!.Retryable && string.Equals(found.Remote.Status, NotSentLineStatus, StringComparison.Ordinal))
            {
                notSent++;
                sendLog.Settle(line, PriceStockSendResults.Failed, "PRICE_INVENTORY_NOT_SENT", "Gönderim yarıda kesildi; bu satır pazaryerine gönderilmedi, aynı değerler sonraki gönderimde yeniden gidecek.");
                continue;
            }
            // A line whose price was written before its stock write was cut off: the price mark moves on (where it still
            // names the price the line carried), the stock mark stays, so the stock goes again with the next send.
            if (found.Found && string.Equals(found.Remote!.ErrorCode, PriceOnlyAppliedCode, StringComparison.Ordinal))
            {
                notSent++;
                if (PriceCurrent(line) && !line.PriceHeld && !line.PriceOmitted)
                {
                    offer.LastPriceHash = line.PriceHash;
                    PriceInventorySendPolicy.RecordSent(offer, line with { QuantityOmitted = true }, payload.SubmittedAt ?? timeProvider.GetUtcNow());
                    offer.Version++;
                }
                sendLog.Settle(line, PriceStockSendResults.Failed, PriceOnlyAppliedCode, "Fiyat yazıldı, stok gönderilmedi; stok sonraki gönderimde yeniden gidecek.");
                continue;
            }
            // Shopify wrote the stock and refused the price (review of 18 Nov 2026: a price never holds the stock back): the
            // stock mark moves on, the price mark stays, so the price is tried again with the next send.
            if (found.Found && string.Equals(found.Remote!.ErrorCode, StockOnlyAppliedCode, StringComparison.Ordinal))
            {
                failed++;
                if (StockCurrent(line) && !line.StockHeld && !line.QuantityOmitted)
                {
                    offer.LastStockProjectionVersion = line.ProjectionVersion;
                    PriceInventorySendPolicy.RecordSent(offer, line with { PriceOmitted = true }, payload.SubmittedAt ?? timeProvider.GetUtcNow());
                    offer.Version++;
                }
                sendLog.Settle(line, PriceStockSendResults.Failed, StockOnlyAppliedCode, $"Stok yazıldı, fiyat reddedildi: {found.Remote.Status}. Fiyat sonraki gönderimde yeniden denenecek.");
                continue;
            }
            // Trendyol refuses a line it took with the same values within 15 minutes; values it last confirmed are held.
            var repeatOfConfirmed = found.Found && !found.Remote!.Succeeded && isTrendyol
                && (TrendyolStockRules.IsRepeatRequestRefusal(found.Remote.ErrorCode) || TrendyolStockRules.IsRepeatRequestRefusal(found.Remote.Status))
                && PriceInventorySendPolicy.MatchesLastSent(offer, line);
            var lineSucceeded = !found.Found || found.Remote!.Succeeded || repeatOfConfirmed;
            // Only an answered line speaks for the code: Hepsiburada lists the refused ones alone, so a line it leaves
            // out is accepted but proves nothing about the code.
            if (found.Found && found.Remote!.Succeeded) confirmedCodes.Add(line.VariantId);
            else if (found.Found && !repeatOfConfirmed && !found.Remote!.Retryable && MarketplaceVariantLinkHealth.MeansUnknownCode(found.Remote.ErrorCode, found.Remote.Status))
                unknownCodes[line.VariantId] = found.Remote.Status is { Length: > 0 } refusal && !string.Equals(refusal, "REJECTED", StringComparison.OrdinalIgnoreCase) ? refusal : found.Remote.ErrorCode ?? "Pazaryeri kodu tanımadı.";
            if (lineSucceeded)
            {
                // The marketplace holds what the line carried, whatever changed since; the marks move on only where they
                // still name the versions the line was built from, so a newer price or stock is still sent.
                var stockCurrent = StockCurrent(line);
                var priceCurrent = PriceCurrent(line);
                // A part held back by the user's pick was not sent: its mark stays, so a later send still carries it.
                if (priceCurrent && !line.PriceHeld) offer.LastPriceHash = line.PriceHash;
                if (stockCurrent && !line.StockHeld) offer.LastStockProjectionVersion = line.ProjectionVersion;
                PriceInventorySendPolicy.RecordSent(offer, line, payload.SubmittedAt ?? timeProvider.GetUtcNow());
                offer.Version++; success++;
                if (!stockCurrent || !priceCurrent)
                {
                    superseded++;
                    sendLog.Settle(line, PriceStockSendResults.Superseded, null, "Pazar yeri satırı aldı; sonuç gelene kadar fiyat veya stok yeni bir değere geçti, yeni değer ayrıca gönderilecek.");
                }
                else sendLog.Settle(line, repeatOfConfirmed ? PriceStockSendResults.Unchanged : PriceStockSendResults.Applied, repeatOfConfirmed ? SafeCode(found.Remote!.ErrorCode) : null);
            }
            else
            {
                sendLog.Settle(line, PriceStockSendResults.Rejected, SafeCode(found.Remote!.ErrorCode) ?? "PRICE_INVENTORY_LINE_REJECTED", RemoteRefusalReason(found.Remote));
                failed++; await RecordIssue(tenantId, $"price-inventory:{connectionId}:{line.Barcode}:{SafeCode(found.Remote!.ErrorCode)}", SafeCode(found.Remote.ErrorCode) ?? "PRICE_INVENTORY_LINE_REJECTED", $"Pazar yeri fiyat-stok satırı reddedildi: {line.Barcode}{(found.Remote.Status is { Length: > 0 } reason && !string.Equals(reason, "FAIL", StringComparison.OrdinalIgnoreCase) ? " — " + reason : "")}.", cancellationToken);
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        if (unknownCodes.Count > 0 || confirmedCodes.Count > 0)
        {
            await RecordPushedVariantLinkHealth(tenantId, connectionId, unknownCodes, confirmedCodes, payload.SubmittedAt, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }
        // A code refused as unknown is looked up at the marketplace by itself: a well-formed answer without it breaks
        // the link at once, one with it heals the link, and anything else leaves the strikes above to decide.
        await QueueListingLookupsForRefusedCodes(tenantId, connectionId, unknownCodes.Keys.ToArray(), cancellationToken);
        if (uncertain > 0) return UncertainLinesResult(uncertain, failed, payload.ExternalOperationId);
        if (failed == 0) return NotSentNote(notSent) ?? SupersededNote(superseded) ?? JobExecutionResult.Success();
        return success == 0 ? JobExecutionResult.Blocked("PRICE_INVENTORY_REJECTED", "Pazar yeri fiyat-stok batch içindeki tüm satırları reddetti.", payload.ExternalOperationId) : JobExecutionResult.ManualReview("PRICE_INVENTORY_PARTIAL_FAILURE", "Pazar yeri fiyat-stok batch kısmi başarısızlıkla tamamlandı.", payload.ExternalOperationId);
    }

    // ADAPTOR-DIGER-1: the line answers of a send cut off part-way (ShopifyHttpClient.InterruptedPriceInventory).
    internal const string NotSentLineStatus = "NOT_SENT";
    internal const string PriceOnlyAppliedCode = "SHOPIFY_PRICE_ONLY_APPLIED";
    internal const string StockOnlyAppliedCode = "SHOPIFY_STOCK_ONLY_APPLIED";

    // A send cut off part-way: done for what it sent, with a note naming what goes again with the next send.
    private static JobExecutionResult? NotSentNote(int notSent) => notSent == 0 ? null
        : JobExecutionResult.SuccessWithNote("PRICE_INVENTORY_LINES_NOT_SENT", $"Gönderim yarıda kesildi; {notSent} satırın fiyatı veya stoğu gönderilmedi ve sonraki gönderimde yeniden gidecek.");

    // A batch the marketplace took whole, some of whose lines changed while it was polled: done, with a note.
    private static JobExecutionResult? SupersededNote(int superseded) => superseded == 0 ? null
        : JobExecutionResult.SuccessWithNote("PRICE_INVENTORY_LINES_SUPERSEDED", $"Pazar yeri tüm satırları aldı; {superseded} satırın fiyatı veya stoğu sonuç gelene kadar değişti, yeni değerleri ayrıca gönderilecek.");

    // Pazarama takes price and stock in separate requests, so each is recorded on its own: a barcode whose
    // stock was accepted keeps that even when its price was refused, and the next send carries only what
    // is still due (Pazarama refuses a value it already holds).
    private async Task<JobExecutionResult> RecordPazaramaPriceInventory(Guid tenantId, Guid connectionId, Guid jobId, PriceInventoryJobPayload payload, IReadOnlyDictionary<Guid, ChannelOffer> offers, IReadOnlyDictionary<string, RemoteOperationLine> lineByBarcode, PendingSendLog sendLog, Func<PriceInventoryPushLine, bool> stockCurrent, Func<PriceInventoryPushLine, bool> priceCurrent, CancellationToken cancellationToken)
    {
        // A value Pazarama refused because it already holds it is recorded as sent. A list split after such a refusal
        // carries one part per piece, so each barcode is judged by the part that carried it.
        var outcomeOf = MarketplaceHub.Infrastructure.Adapters.Pazarama.PazaramaBatchMapper.LineOutcomes(payload.ExternalOperationId!, payload.PayloadJson);
        var success = 0; var failed = 0; var superseded = 0; var uncertain = 0;
        var unknownCodes = new Dictionary<Guid, string>();
        var confirmedCodes = new List<Guid>();
        foreach (var line in payload.Lines)
        {
            lineByBarcode.TryGetValue(line.Barcode, out var remote);
            var (priceOk, stockOk, notSentCode, missingResult) = outcomeOf(line.Barcode, remote);
            // A barcode a sent request's result does not name: uncertain, not recorded as sent (as on the other
            // marketplaces), while the barcodes it names are recorded.
            if (missingResult)
            {
                uncertain++;
                var missing = $"Pazarama fiyat-stok sonucu {line.Barcode} için satır vermedi; sonuç belirsiz, gönderilmiş sayılmadı.";
                await RecordIssue(tenantId, $"price-inventory:{connectionId}:{line.Barcode}:PRICE_INVENTORY_LINE_UNCONFIRMED", "PRICE_INVENTORY_LINE_UNCONFIRMED", missing, cancellationToken);
                sendLog.Settle(line, PriceStockSendResults.Unknown, "PRICE_INVENTORY_LINE_UNCONFIRMED", missing);
                continue;
            }
            var code = remote?.ErrorCode;
            var offer = offers[line.OfferId];
            // Per line, as on the other marketplaces: what Pazarama took is its confirmed value, and a mark moves on only
            // while it still names the version the line was built from.
            var priceStill = priceCurrent(line);
            var stockStill = stockCurrent(line);
            if (priceOk && priceStill) offer.LastPriceHash = line.PriceHash;
            if (stockOk && stockStill) offer.LastStockProjectionVersion = line.ProjectionVersion;
            if (priceOk || stockOk) offer.Version++;
            var taken = line with { PriceOmitted = line.PriceOmitted || !priceOk, QuantityOmitted = line.QuantityOmitted || !stockOk };
            if (!taken.PriceOmitted || !taken.QuantityOmitted) PriceInventorySendPolicy.RecordSent(offer, taken, payload.SubmittedAt ?? timeProvider.GetUtcNow());
            // Only Pazarama's own answer for the barcode speaks for the code, as on the other marketplaces. Its error code
            // only names the request that refused (PAZARAMA_PRICE_REJECTED, …), so Pazarama's message alone is judged.
            if (priceOk && stockOk && remote is { Succeeded: true }) confirmedCodes.Add(line.VariantId);
            else if (remote is { Succeeded: false, Retryable: false, Status: { Length: > 0 } } && MarketplaceVariantLinkHealth.MeansUnknownCode(null, remote.Status))
                unknownCodes[line.VariantId] = remote.Status!;
            if (priceOk && stockOk && (!priceStill || !stockStill))
            {
                success++; superseded++;
                sendLog.Settle(line, PriceStockSendResults.Superseded, null, "Pazar yeri satırı aldı; sonuç gelene kadar fiyat veya stok yeni bir değere geçti, yeni değer ayrıca gönderilecek.");
                continue;
            }
            if (priceOk && stockOk)
            {
                success++;
                // Pazarama took the price but holds it for its own approval: the send log says so instead of a bare "applied".
                var awaitsApproval = string.Equals(remote?.Status, PazaramaBatchMapper.PendingApprovalStatus, StringComparison.Ordinal);
                sendLog.Settle(line, PriceStockSendResults.Applied, null, awaitsApproval ? "Pazarama fiyatı onaya gönderdi; onaylanana kadar mağazada önceki fiyat görünür." : null);
                continue;
            }
            failed++;
            var issueCode = SafeCode(code ?? notSentCode) ?? "PRICE_INVENTORY_LINE_REJECTED";
            var what = !priceOk && !stockOk ? "fiyat ve stok" : !priceOk ? "fiyat" : "stok";
            var refusal = $"Pazarama {what} güncellemesini almadı: {line.Barcode}{(remote?.Status is { Length: > 0 } reason && !string.Equals(reason, "FAIL", StringComparison.OrdinalIgnoreCase) ? " — " + reason : "")}.";
            await RecordIssue(tenantId, $"price-inventory:{connectionId}:{line.Barcode}:{issueCode}", issueCode, refusal, cancellationToken);
            sendLog.Settle(line, PriceStockSendResults.Rejected, issueCode, refusal);
        }
        await db.SaveChangesAsync(cancellationToken);
        if (unknownCodes.Count > 0 || confirmedCodes.Count > 0)
        {
            await RecordPushedVariantLinkHealth(tenantId, connectionId, unknownCodes, confirmedCodes, payload.SubmittedAt, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }
        await QueueListingLookupsForRefusedCodes(tenantId, connectionId, unknownCodes.Keys.ToArray(), cancellationToken);
        if (uncertain > 0) return UncertainLinesResult(uncertain, failed, payload.ExternalOperationId);
        if (failed == 0) return SupersededNote(superseded) ?? JobExecutionResult.Success();
        return success == 0 ? JobExecutionResult.Blocked("PRICE_INVENTORY_REJECTED", "Pazar yeri fiyat-stok batch içindeki tüm satırları reddetti.", payload.ExternalOperationId) : JobExecutionResult.ManualReview("PRICE_INVENTORY_PARTIAL_FAILURE", "Pazar yeri fiyat-stok batch kısmi başarısızlıkla tamamlandı.", payload.ExternalOperationId);
    }

    // Lines the answer did not name are uncertain: the job asks for a look, saying how many, beside any refusals.
    internal static JobExecutionResult UncertainLinesResult(int uncertain, int refused, string? operationId) =>
        JobExecutionResult.ManualReview("PRICE_INVENTORY_LINES_UNCONFIRMED",
            $"Pazar yeri fiyat-stok sonucu {uncertain} satırı adlandırmadı; bu satırların sonucu belirsiz ve gönderilmiş sayılmadı{(refused > 0 ? $", {refused} satır reddedildi" : "")}.", operationId);

    // The adapter's own code and message stay in the summary, so the cause of an uncertain create can be read.
    internal static string AmbiguousCreateSummary(AdapterError error) =>
        $"Create çağrısının uzak tarafta uygulanıp uygulanmadığı kesinleştirilemedi. Pazaryeri yanıtı: {error.Code}: {error.SafeMessage}";
    private static bool IsRepeatRequestRefusal(AdapterError error) =>
        error.Code is TrendyolStockRules.RepeatRequestWaitCode or PttAvmProductPayloadRules.RepeatRequestWaitCode;

    // A poll that ends the same way for every line of its request writes that outcome to the request's send log rows.
    private async Task<JobExecutionResult> SettleSendLogAsync(Guid tenantId, Guid jobId, PriceInventoryJobPayload payload, string outcome, JobExecutionResult result, CancellationToken cancellationToken)
    {
        await PriceStockSendLogRecorder.SettleAllAsync(db, tenantId, jobId, payload.ExternalOperationId, outcome, result.ErrorCode, result.ErrorSummary, timeProvider.GetUtcNow(), cancellationToken);
        return result;
    }

    // The marketplace's own words for a refused line; a bare FAIL/REJECTED says nothing more than the result.
    private static string? RemoteRefusalReason(RemoteOperationLine remote) =>
        remote.Status is { Length: > 0 } reason && !string.Equals(reason, "FAIL", StringComparison.OrdinalIgnoreCase) && !string.Equals(reason, "REJECTED", StringComparison.OrdinalIgnoreCase) ? reason : null;

    // A price/stock request refused as a repeat: when every line still carries the current versions and the values the
    // marketplace last confirmed for its code, the marks move on and nothing is sent. Otherwise nothing changes.
    private async Task<bool> SettleRepeatedPriceInventoryAsync(Guid tenantId, Guid connectionId, IReadOnlyList<PriceInventoryPushLine> lines, CancellationToken cancellationToken)
    {
        if (lines.Count == 0) return false;
        var offerIds = lines.Select(x => x.OfferId).Distinct().ToArray();
        var offers = await db.ChannelOffers.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && offerIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        var variantIds = lines.Select(x => x.VariantId).Distinct().ToArray();
        var projections = await db.InventoryItems.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.LocationCode == "MAIN" && variantIds.Contains(x.VariantId))
            .ToDictionaryAsync(x => x.VariantId, x => x.ProjectionVersion, cancellationToken);
        foreach (var line in lines)
            if (!offers.TryGetValue(line.OfferId, out var offer) || !projections.TryGetValue(line.VariantId, out var projection)
                || !PriceInventorySendPolicy.StockCurrent(line, projection, offer.SafetyStock) || !PriceInventorySendPolicy.PriceCurrent(line, offer.PriceVersion)
                || !PriceInventorySendPolicy.MatchesLastSent(offer, line))
                return false;
        foreach (var line in lines)
        {
            var offer = offers[line.OfferId];
            if (!line.PriceHeld) offer.LastPriceHash = line.PriceHash;
            if (!line.StockHeld) offer.LastStockProjectionVersion = line.ProjectionVersion;
            offer.Version++;
        }
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    internal async Task<JobExecutionResult> DispatchStockProjection(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, Guid? jobId, CancellationToken cancellationToken)
    {
        Guid variantId; string trigger;
        try
        {
            using var payload = JsonDocument.Parse(payloadJson);
            variantId = payload.RootElement.GetProperty("variantId").GetGuid();
            // The change that queued the dispatch names why the send goes (the send log's trigger).
            trigger = PriceStockSendTriggers.FromSourceEvent(payload.RootElement.TryGetProperty("sourceEventId", out var source) && source.ValueKind == JsonValueKind.String ? source.GetString() : null);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { return JobExecutionResult.Blocked("STOCK_PROJECTION_PAYLOAD_INVALID", "Stok projection işi geçersiz payload içeriyor."); }
        var (blocked, decision) = await StockProjectionGateAsync(tenantId, connectionId, cancellationToken);
        if (blocked is not null) return blocked;
        if (decision == StockProjectionDispatchDecision.Skip) return JobExecutionResult.SuccessWithNote("AUTOMATIC_WRITES_OFF", "Otomatik fiyat-stok yazması kapalı; pazaryerine gönderilmedi.");
        if (!await db.ChannelOffers.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.VariantId == variantId && x.Status == "ACTIVE", cancellationToken)) return JobExecutionResult.Success();
        if (decision == StockProjectionDispatchDecision.Dispatch && await OrderFreshnessWaitAsync(tenantId, connectionId, jobId, cancellationToken) is { } wait) return wait;
        return await DispatchStockProjectionVariant(tenantId, connectionId, variantId, decision, correlationId, trigger, cancellationToken);
    }

    // Owner decision C: an automatic send waits (bounded) for an order read of the connection that started after the
    // change was queued, so an order placed before the change is taken off the panel before the absolute number goes out.
    // The job's own creation time is the change's time: the outbox queues it in the same save as the change.
    private async Task<JobExecutionResult?> OrderFreshnessWaitAsync(Guid tenantId, Guid connectionId, Guid? jobId, CancellationToken cancellationToken)
    {
        if (jobId is not { } id) return null;
        var changedAt = await db.IntegrationJobs.AsNoTracking().Where(x => x.Id == id).Select(x => (DateTimeOffset?)x.CreatedAt).SingleOrDefaultAsync(cancellationToken);
        var readsOrders = await db.ConnectionSyncPolicies.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "ORDERS" && x.Enabled, cancellationToken);
        var cursor = await db.SyncCursors.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == OrderFreshnessGatePolicy.OrderReadCursor)
            .Select(x => new { x.LastAttemptAt, x.LastSuccessAt })
            .SingleOrDefaultAsync(cancellationToken);
        var maxWait = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("MarketplaceSync:StockPush:OrderFreshnessMaxWaitSeconds", (int)OrderFreshnessGatePolicy.DefaultMaxWait.TotalSeconds), 0, 3_600));
        var now = timeProvider.GetUtcNow();
        var decision = OrderFreshnessGatePolicy.Decide(readsOrders && cursor is not null, changedAt, cursor?.LastAttemptAt, cursor?.LastSuccessAt, now, maxWait);
        // Webhook first: healthy order notifications cover the change once they have had time to arrive and every one
        // received has been ingested, without waiting for the (stretched) scheduled read.
        if (decision == OrderFreshnessDecision.Wait && OrderFreshnessGatePolicy.NotificationGraceElapsed(changedAt, now)
            && await ScheduledJobProducer.WebhookHealthyAsync(db, tenantId, connectionId, now, cancellationToken))
        {
            var notificationsPending = await db.IntegrationJobs.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId
                && (x.JobType == MarketplaceJobTypes.WebhookIngest || x.JobType == MarketplaceJobTypes.ShopifyWebhookIngest || x.JobType == MarketplaceJobTypes.HepsiburadaWebhookIngest)
                && (x.Status == JobStatus.Pending || x.Status == JobStatus.Leased || x.Status == JobStatus.RetryScheduled), cancellationToken);
            decision = OrderFreshnessGatePolicy.Decide(readsOrders && cursor is not null, changedAt, cursor?.LastAttemptAt, cursor?.LastSuccessAt, now, maxWait, webhookHealthy: true, notificationsPending);
        }
        return decision == OrderFreshnessDecision.Wait
            ? JobExecutionResult.Deferred(OrderFreshnessGatePolicy.WaitCode, OrderFreshnessGatePolicy.WaitMessage, OrderFreshnessGatePolicy.RecheckDelay)
            : null;
    }

    // Whether an automatic stock write may go out on the connection at all; read once per reconcile pass
    // instead of once per candidate. A write policy without a row is off (owner decision A).
    private async Task<(JobExecutionResult? Blocked, StockProjectionDispatchDecision Decision)> StockProjectionGateAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId, cancellationToken);
        if (connection is null) return (JobExecutionResult.Blocked("CONNECTION_NOT_FOUND", "Bağlantı bulunamadı."), StockProjectionDispatchDecision.Skip);
        // Stock projection is an automatic write path. Stage manual writes remain
        // available, but an automatic projection must never enqueue a remote write
        // while the explicit external-write switch is off.
        if (!WritesEnabled(connection.SettingsJson)) return (null, StockProjectionDispatchDecision.Skip);
        return (null, StockProjectionOutboxPolicy.Decide(
            await StrictWritePolicyEnabledAsync(tenantId, connectionId, MarketplaceExternalWritePolicies.Stock, cancellationToken),
            await StrictWritePolicyEnabledAsync(tenantId, connectionId, MarketplaceExternalWritePolicies.Price, cancellationToken)));
    }

    private async Task<JobExecutionResult> DispatchStockProjectionVariant(Guid tenantId, Guid connectionId, Guid variantId, StockProjectionDispatchDecision decision, string correlationId, string trigger, CancellationToken cancellationToken)
    {
        if (decision == StockProjectionDispatchDecision.HoldForPricePolicy)
        {
            // The request would carry the price too; sending it with the price policy off could
            // overwrite a price the seller manages on the marketplace. The stock waits, visibly.
            await RecordIssue(tenantId, $"stock-projection-price-policy:{connectionId:N}", "STOCK_WRITE_HELD_PRICE_POLICY_DISABLED", "Stok değişikliği pazaryerine gönderilmedi: fiyat-stok isteği fiyatı da taşıdığı için fiyat dış yazma akışı da açık olmalı. Fiyat akışı açıldığında stok kontrol taramasıyla gönderilir.", cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return JobExecutionResult.Success();
        }
        var composer = new PriceInventoryComposer(db);
        var build = await composer.BuildAsync(tenantId, connectionId, cancellationToken, variantId, automatic: true);
        if (!build.Succeeded)
        {
            var code = build.Error!.Code;
            if (code == "INVENTORY_REQUIRED")
            {
                await RecordIssue(tenantId, $"price-inventory-inventory-required:{connectionId:N}", code, build.Error.Message, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }
            // An offer the user has to fix stays visible as an issue instead of a blocked job (G: no silent freeze).
            if (PriceInventoryOfferIssues.IsOfferProblem(code))
            {
                foreach (var invalid in composer.InvalidOffers)
                    await RecordOfferIssue(tenantId, connectionId, invalid.VariantId, invalid.Code, invalid.Message, cancellationToken);
                if (composer.InvalidOffers.Count == 0) await RecordOfferIssue(tenantId, connectionId, variantId, code, build.Error.Message, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                return JobExecutionResult.Success();
            }
            if (!StockProjectionOutboxPolicy.IsSettledBuildFailure(code)) return JobExecutionResult.Blocked(code, build.Error.Message);
            // Already sent or not live on this connection: nothing to send now.
            return JobExecutionResult.Success();
        }
        return await QueueAutomaticPushAsync(tenantId, connectionId, build.Value!, variantId, correlationId, trigger, cancellationToken);
    }

    // One automatic PRICE_INVENTORY_SYNC job for a built draft (one variant's change or a reconcile batch). Offers the
    // build left out are reported; the offers it carries are fine again, so their offer issues close.
    private async Task<JobExecutionResult> QueueAutomaticPushAsync(Guid tenantId, Guid connectionId, PriceInventoryDraft draft, Guid? variantId, string correlationId, string trigger, CancellationToken cancellationToken)
    {
        foreach (var invalid in draft.InvalidOffers)
            await RecordOfferIssue(tenantId, connectionId, invalid.VariantId, invalid.Code, invalid.Message, cancellationToken);
        await ResolveOfferIssuesAsync(tenantId, connectionId, draft.Lines.Select(line => line.VariantId).Distinct().ToArray(), cancellationToken);
        var dedup = PriceInventoryOutboxPolicy.DedupKey(connectionId, draft.Lines);
        var now = timeProvider.GetUtcNow();
        // A failed job of the same values gives up its key in the same save as the new job (Q-1/K-1).
        var existing = await db.IntegrationJobs.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.JobType == MarketplaceJobTypes.PriceInventorySync && x.JobDedupKey == dedup, cancellationToken);
        if (existing is not null)
        {
            if (!PriceInventoryOutboxPolicy.AutomaticSendMayRetire(existing.Status, existing.LastErrorCode, existing.CompletedAt, now))
            {
                await db.SaveChangesAsync(cancellationToken);
                return JobExecutionResult.Success();
            }
            existing.JobDedupKey = PriceInventoryOutboxPolicy.RetiredDedupKey(dedup, existing.Id); existing.Version++;
        }
        var id = Guid.CreateVersion7();
        var jobPayload = JsonSerializer.Serialize(new PriceInventoryJobPayload(id, connectionId, "SUBMIT", draft.PayloadHash, draft.PayloadJson, draft.Lines, null, null, variantId, Automatic: true, Trigger: trigger));
        var writeDelay = await ExternalWriteDelaySecondsAsync(tenantId, connectionId, MarketplaceExternalWritePolicies.Stock, cancellationToken);
        Jobs.JobEnqueuer.Add(db, new IntegrationJobRequest(tenantId, connectionId, MarketplaceJobTypes.PriceInventorySync, jobPayload, dedup, correlationId, now.AddSeconds(writeDelay)) { Id = id, Priority = JobPriorityPolicy.For(MarketplaceJobTypes.PriceInventorySync, userInitiated: true), MaxAttempts = 10, CreatedAt = now });
        await db.SaveChangesAsync(cancellationToken);
        return JobExecutionResult.Success();
    }

    private async Task ResolveOfferIssuesAsync(Guid tenantId, Guid connectionId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken)
    {
        if (variantIds.Count == 0) return;
        var keys = variantIds.Select(variantId => PriceInventoryOfferIssues.Key(connectionId, variantId)).ToArray();
        var now = timeProvider.GetUtcNow();
        foreach (var issue in await db.OperationalIssues.Where(x => x.TenantId == tenantId && keys.Contains(x.DedupeKey) && x.Status != IssueStatus.Resolved).ToListAsync(cancellationToken))
        {
            issue.Status = IssueStatus.Resolved;
            issue.ResolvedAt = now;
        }
    }

    // F: the catch-up for every platform. One pass builds one batch of the offers whose price or stock was not sent
    // (the platform's own batch limit: Trendyol 1,000, N11 and Pazarama their request caps, Hepsiburada its upload
    // limit, PTT AVM 1,000 products) and queues one push, so the platform's request rate is one request per pass.
    // A pass waits while an automatic push of the connection is still on its way, and (decision C) until an order read
    // started after the pass was queued has finished. Offers it cannot send are reported, not a reason to stop.
    internal async Task<JobExecutionResult> ReconcileStock(Guid tenantId, Guid connectionId, string correlationId, Guid? jobId, CancellationToken cancellationToken)
    {
        var (blocked, decision) = await StockProjectionGateAsync(tenantId, connectionId, cancellationToken);
        if (blocked is not null || decision == StockProjectionDispatchDecision.Skip)
            return JobExecutionResult.SuccessWithNote("AUTOMATIC_WRITES_OFF", "Otomatik fiyat-stok yazması kapalı; stok uzlaştırması gönderim yapmadı.");
        if (decision == StockProjectionDispatchDecision.HoldForPricePolicy)
        {
            // Nothing goes out while the price policy is off: one held candidate is enough to show the issue.
            var held = await db.ChannelOffers.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Status == "ACTIVE")
                .OrderBy(x => x.VariantId).Select(x => (Guid?)x.VariantId).FirstOrDefaultAsync(cancellationToken);
            if (held is { } heldVariant) await DispatchStockProjectionVariant(tenantId, connectionId, heldVariant, decision, correlationId, PriceStockSendTriggers.Reconcile, cancellationToken);
            return JobExecutionResult.Success();
        }
        if (await AutomaticPushInFlightAsync(tenantId, connectionId, cancellationToken))
            return JobExecutionResult.SuccessWithNote("PRICE_INVENTORY_PUSH_IN_FLIGHT", "Bağlantının önceki fiyat-stok gönderimi sürüyor; uzlaştırma bir sonraki turda devam edecek.");
        if (await OrderFreshnessWaitAsync(tenantId, connectionId, jobId, cancellationToken) is { } wait) return wait;

        var composer = new PriceInventoryComposer(db);
        var build = await composer.BuildAsync(tenantId, connectionId, cancellationToken, automatic: true);
        if (!build.Succeeded)
        {
            var code = build.Error!.Code;
            if (code == "INVENTORY_REQUIRED")
                await RecordIssue(tenantId, $"price-inventory-inventory-required:{connectionId:N}", code, build.Error.Message, cancellationToken);
            // Every offer the pass could send is one the user has to fix: each is reported by itself.
            foreach (var invalid in composer.InvalidOffers)
                await RecordOfferIssue(tenantId, connectionId, invalid.VariantId, invalid.Code, invalid.Message, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            if (StockProjectionOutboxPolicy.IsSettledBuildFailure(code) || code == "INVENTORY_REQUIRED" || PriceInventoryOfferIssues.IsOfferProblem(code)) return JobExecutionResult.Success();
            return JobExecutionResult.Blocked(code, build.Error.Message);
        }
        if (build.Value!.MissingInventorySkus.Count > 0)
            await RecordIssue(tenantId, $"price-inventory-inventory-required:{connectionId:N}", "INVENTORY_REQUIRED", PriceInventoryComposer.MissingInventoryMessage(build.Value.MissingInventorySkus), cancellationToken);
        // ReconciledAt is an observation watermark, not an enqueue timestamp. It is updated only when a remote catalog
        // read observes stock; a disabled, blocked, or merely queued write remains visible as stale until then.
        return await QueueAutomaticPushAsync(tenantId, connectionId, build.Value, null, correlationId, PriceStockSendTriggers.Reconcile, cancellationToken);
    }

    // An automatic price/stock push of the connection that is queued, running or retrying.
    private Task<bool> AutomaticPushInFlightAsync(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) =>
        db.IntegrationJobs.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId
            && x.JobType == MarketplaceJobTypes.PriceInventorySync && x.TriggerType != "user"
            && (x.Status == JobStatus.Pending || x.Status == JobStatus.Leased || x.Status == JobStatus.RetryScheduled), cancellationToken);

    // A price/stock answer for one line: a code the marketplace does not know breaks the link, and an explicit
    // acceptance heals one a refusal broke. A code saved by hand after the push was sent is left alone.
    private async Task RecordPushedVariantLinkHealth(Guid tenantId, Guid connectionId, IReadOnlyDictionary<Guid, string> rejected, IReadOnlyCollection<Guid> accepted, DateTimeOffset? sentAt, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var since = sentAt ?? now;
        var broken = 0;
        foreach (var (variantId, reason) in rejected)
        {
            var safeReason = Short(reason, 200);
            var target = db.MarketplaceVariantLinks
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.VariantId == variantId && (x.HealthChangedAt == null || x.HealthChangedAt < since));
            // A second refusal for an unknown code breaks the link; the first only makes it suspect (still linked).
            broken += await target.Where(x => x.HealthStatus == MarketplaceVariantLinkHealth.Suspect)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.HealthStatus, MarketplaceVariantLinkHealth.Rejected)
                    .SetProperty(x => x.HealthReason, safeReason)
                    .SetProperty(x => x.HealthChangedAt, now), cancellationToken);
            await target.Where(x => x.HealthStatus == null || x.HealthStatus == MarketplaceVariantLinkHealth.Unverified)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.HealthStatus, MarketplaceVariantLinkHealth.Suspect)
                    .SetProperty(x => x.HealthReason, safeReason)
                    .SetProperty(x => x.HealthChangedAt, now), cancellationToken);
        }
        if (accepted.Count > 0)
            await db.MarketplaceVariantLinks
                .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && accepted.Contains(x.VariantId)
                    && (x.HealthStatus == MarketplaceVariantLinkHealth.Rejected || x.HealthStatus == MarketplaceVariantLinkHealth.Suspect))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.HealthStatus, (string?)null)
                    .SetProperty(x => x.HealthReason, (string?)null)
                    .SetProperty(x => x.HealthChangedAt, now), cancellationToken);
        if (broken > 0)
        {
            var platformCode = await ConnectionPlatformCode(tenantId, connectionId, cancellationToken);
            await RecordIssue(tenantId, $"variant-link-rejected:{connectionId}", "VARIANT_LINK_REJECTED",
                $"{MarketplacePlatformName(platformCode)}: {broken:N0} varyantın kodu fiyat-stok gönderiminde art arda iki kez pazaryerinde bulunamadı; bu varyantlara toplu fiyat ve stok gönderilmez. Kodu ürünün Varyant Eşleştirme penceresinden düzeltin.", cancellationToken);
        }
    }
}
