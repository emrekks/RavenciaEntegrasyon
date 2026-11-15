using System.Data;
using System.Security.Cryptography;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed partial class MarketplaceSalesService
{
    public async Task<ServiceResult<ReturnDetailView>> MarkReturnReceivedAsync(Guid tenantId, Guid userId, Guid claimId, long expectedVersion, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        var claim = await db.ReturnClaims.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == claimId
            && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == x.ConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")), cancellationToken);
        if (claim is null) return NotFound<ReturnDetailView>();
        if (claim.Version != expectedVersion) return Precondition<ReturnDetailView>(claim.Version);
        if (claim.Status is not (ReturnClaimStatus.Requested or ReturnClaimStatus.InTransit)) return ServiceResult<ReturnDetailView>.Fail("RETURN_RECEIPT_NOT_ALLOWED", "Teslim alındı işlemi yalnız talep oluşturulan veya kargodaki iadeler için kullanılabilir.", 409);
        var platformCode = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == claim.ConnectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(cancellationToken);
        if (!ReturnReceiptAllowed(platformCode)) return ServiceResult<ReturnDetailView>.Fail("RETURN_RECEIPT_NOT_ALLOWED", "Bu pazaryerinde iade kararı, pazaryeri iadeyi aksiyon bekliyor durumuna aldığında açılır; teslim alındı işareti kullanılmaz.", 409);
        if (!ReturnClaimStateMachine.CanTransition(claim.Status, ReturnClaimStatus.ActionRequired)) return ServiceResult<ReturnDetailView>.Fail("RETURN_STATE_CONFLICT", "İade durumu aksiyon bekliyor durumuna geçirilemedi.", 409);

        var now = timeProvider.GetUtcNow();
        claim.Status = ReturnClaimStatus.ActionRequired;
        claim.RawStatus = "RECEIVED_BY_SELLER";
        claim.UpdatedAt = now;
        claim.Version++;
        db.AuditLogs.Add(new AuditLog
        {
            TenantId = tenantId,
            ActorUserId = userId,
            Action = "RETURN_RECEIVED_BY_SELLER",
            TargetType = "ReturnClaim",
            TargetId = claimId.ToString("D"),
            Reason = "Kargo fiziksel olarak teslim alındı; iade inceleme aksiyonları açıldı.",
            CorrelationId = correlationId,
            CreatedAt = now
        });
        await db.SaveChangesAsync(cancellationToken);
        return await ReturnAsync(tenantId, claimId, cancellationToken);
    }

    public async Task<ServiceResult<ReturnDetailView>> ProcessReturnActionInstantAsync(Guid tenantId, Guid userId, Guid claimId, long expectedVersion, ReturnDecisionCommand command, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        var normalizedKey = idempotencyKey.Trim();
        var prior = await db.ReturnDecisions.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.IdempotencyKey == normalizedKey, cancellationToken);
        if (prior is not null)
        {
            if (prior.ClaimId != claimId) return ServiceResult<ReturnDetailView>.Fail("IDEMPOTENCY_KEY_CONFLICT", "Bu istek anahtarı başka bir iade için kullanılmış; sayfayı yenileyip yeniden deneyin.", 409);
            if (prior.Status is "SUCCEEDED" or "SUBMITTED") return await ReturnAsync(tenantId, claimId, cancellationToken);
            if (prior.Status is "PENDING" or "MANUAL_REVIEW") return ServiceResult<ReturnDetailView>.Fail("RETURN_DECISION_IN_PROGRESS", "Bu iade kararının sonucu henüz kesinleşmedi.", 409);
            return ServiceResult<ReturnDetailView>.Fail(prior.ErrorCode ?? "RETURN_ACTION_FAILED", "Bu Idempotency-Key ile başlatılan iade kararı başarısız oldu; yeni bir anahtar ile tekrar deneyin.", 409);
        }

        var claim = await db.ReturnClaims.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == claimId
            && db.PlatformConnections.Any(connection => connection.TenantId == tenantId && connection.Id == x.ConnectionId && (connection.Status == "ACTIVE" || connection.Status == "VERIFIED")), cancellationToken);
        if (claim is null) return NotFound<ReturnDetailView>();
        var platformCode = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == claim.ConnectionId).Select(x => x.PlatformCode).SingleAsync(cancellationToken);
        var isHepsiburada = string.Equals(platformCode, "HEPSIBURADA", StringComparison.OrdinalIgnoreCase);
        var effectType = isHepsiburada ? "HEPSIBURADA_INSTANT_RETURN_ACTION" : "TRENDYOL_INSTANT_RETURN_ACTION";
        if (claim.Version != expectedVersion) return Precondition<ReturnDetailView>(claim.Version);
        var action = command.Action.Trim().ToUpperInvariant();
        var preApprovalConfirm = action == "PREAPPROVAL_CONFIRM";
        var awaitingPreApproval = isHepsiburada && HepsiburadaReturnActionPolicy.IsAwaitingPreApproval(claim.RawStatus);
        if (action is not ("APPROVE" or "REJECT") && !preApprovalConfirm) return Invalid<ReturnDetailView>("action", "İade aksiyonu APPROVE, PREAPPROVAL_CONFIRM veya REJECT olmalıdır.");
        var isN11 = IsN11(platformCode);
        var wholeClaim = ReturnDecisionScope.IsWholeClaim(platformCode);
        if (action == "REJECT" && isN11 && N11ReturnDenyRule.Problem(command.ReasonCode, command.Explanation, command.ReturnShipment?.ShipmentCompanyId, command.ReturnShipment?.TrackingNumber) is { } denyProblem)
            return Invalid<ReturnDetailView>("returnShipment", denyProblem);
        if (action == "REJECT" && isN11 && command.EvidenceAssetIds is { Count: > 0 }) return Invalid<ReturnDetailView>("evidenceAssetIds", "N11 reddi kanıt dosyası almaz.");
        if (command.ReturnShipment is not null && !(action == "REJECT" && isN11)) return Invalid<ReturnDetailView>("returnShipment", "Geri gönderim bilgisi yalnız N11 iadesi reddedilirken gönderilir.");
        if (preApprovalConfirm && !awaitingPreApproval) return ServiceResult<ReturnDetailView>.Fail("HEPSIBURADA_PREAPPROVAL_ACTION_NOT_ALLOWED", "İnceleme için geri gönderim yalnız Hepsiburada AwaitingPreApproval durumunda kullanılabilir.", 409);
        if (claim.Status != ReturnClaimStatus.ActionRequired && !(awaitingPreApproval && claim.Status == ReturnClaimStatus.Requested)) return ServiceResult<ReturnDetailView>.Fail("RETURN_ACTION_NOT_ALLOWED", "İade aksiyonu yalnız sağlayıcının aksiyon beklediği durumda kullanılabilir.", 409);

        var claimLines = await db.ReturnLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.ClaimId == claimId).Select(x => new { x.Id, x.RawStatus }).ToListAsync(cancellationToken);
        var claimLineIds = claimLines.Select(x => x.Id).ToList();
        if (claimLineIds.Count == 0) return Invalid<ReturnDetailView>("returnLineIds", "İade işleminde en az bir ürün satırı bulunmalıdır.");
        // Lines the marketplace already settled (for example one accepted on Trendyol) are not sent again.
        var actionableLineIds = isHepsiburada ? claimLineIds : claimLines.Where(x => ReturnLineStatusPolicy.IsActionable(x.RawStatus)).Select(x => x.Id).ToList();
        if (actionableLineIds.Count == 0) return ServiceResult<ReturnDetailView>.Fail("RETURN_ACTION_NOT_ALLOWED", "Bu iadenin işlem bekleyen ürün satırı kalmadı.", 409);
        // Only an open decision on the same lines holds this one back; the claim's other lines take their own decision.
        var openDecisionLines = await OpenReturnDecisions(tenantId, claimId, isHepsiburada).Select(x => x.ReturnLineIds).ToListAsync(cancellationToken);
        // Without a selection the decision takes the waiting lines that no open decision covers yet.
        var freeLineIds = isHepsiburada ? actionableLineIds : actionableLineIds.Where(lineId => !openDecisionLines.Any(lineIds => ReturnDecisionLinePolicy.Overlaps(lineIds, [lineId]))).ToList();
        var returnLineIds = command.ReturnLineIds?.Distinct().ToArray() ?? (freeLineIds.Count > 0 ? freeLineIds : actionableLineIds).ToArray();
        if (returnLineIds.Length == 0 || returnLineIds.Except(claimLineIds).Any()) return Invalid<ReturnDetailView>("returnLineIds", "İşlem yapılacak ürün satırları bu iadeye ait olmalıdır.");
        returnLineIds = returnLineIds.Intersect(actionableLineIds).ToArray();
        if (returnLineIds.Length == 0) return ServiceResult<ReturnDetailView>.Fail("RETURN_ACTION_NOT_ALLOWED", "Seçilen ürün satırları pazaryerinde zaten sonuçlanmış.", 409);
        // Hepsiburada and Shopify decide the whole claim (ReturnDecisionScope): a choice of some lines would be sent as
        // the whole claim's decision, so it is refused instead of being recorded as a partial one.
        if (wholeClaim && returnLineIds.Length != actionableLineIds.Count) return Invalid<ReturnDetailView>("returnLineIds", $"{(isHepsiburada ? "Hepsiburada" : "Shopify")} talep kabul/red işlemi talebin tüm ürün satırlarına uygulanır; kısmi satır seçimi desteklenmez.");
        var partialLineDecision = returnLineIds.Length < actionableLineIds.Count;
        var finalizedWith = command.FinalizedWith?.Trim();
        if (isHepsiburada && action == "APPROVE" && finalizedWith is not ("Refund" or "Change")) return Invalid<ReturnDetailView>("finalizedWith", "Hepsiburada talep kabulünde iade veya ürün değişimi seçilmelidir.");
        if ((!isHepsiburada || action != "APPROVE") && !string.IsNullOrWhiteSpace(finalizedWith)) return Invalid<ReturnDetailView>("finalizedWith", "Kabul türü yalnız Hepsiburada talebini kabul ederken gönderilebilir.");
        if (isHepsiburada && action == "APPROVE" && string.Equals(claim.ReasonCode, "MissingInvoice", StringComparison.OrdinalIgnoreCase)) return ServiceResult<ReturnDetailView>.Fail("HEPSIBURADA_MISSING_INVOICE_MANUAL", "Eksik fatura taleplerinin kabul işlemi Hepsiburada panelinden yapılmalıdır.", 409);
        if (openDecisionLines.Any(lineIds => ReturnDecisionLinePolicy.Overlaps(lineIds, returnLineIds))) return ServiceResult<ReturnDetailView>.Fail("RETURN_DECISION_IN_PROGRESS", "Bu iade kararı pazaryerine gönderildi; sonuç kesinleşene kadar yeni bir karar gönderilemez.", 409);
        if (action == "REJECT" && (string.IsNullOrWhiteSpace(command.ReasonCode) || string.IsNullOrWhiteSpace(command.Explanation) || command.Explanation.Trim().Length > 500)) return Invalid<ReturnDetailView>("explanation", "REJECT için reasonCode ve en fazla 500 karakter açıklama gerekir.");
        if (action == "REJECT" && !isHepsiburada && !isN11 && (command.EvidenceAssetIds is null || command.EvidenceAssetIds.Count == 0)
            && RejectEvidenceRequired(command.ReasonCode, await CachedIssueReasonsAsync(tenantId, claim.ConnectionId, correlationId, cancellationToken), platformCode))
            return Invalid<ReturnDetailView>("evidenceAssetIds", "Seçilen ret nedeni için en az bir kanıt dosyası gerekir.");
        if (preApprovalConfirm && (!string.IsNullOrWhiteSpace(command.FinalizedWith) || !string.IsNullOrWhiteSpace(command.ReasonCode) || !string.IsNullOrWhiteSpace(command.Explanation) || command.EvidenceAssetIds is { Count: > 0 })) return Invalid<ReturnDetailView>("action", "Hepsiburada ön onay incelemesi ek kabul, ret veya kanıt alanı almaz.");

        var stage = await IsStageConnection(tenantId, claim.ConnectionId, cancellationToken);
        if (!stage && !await IsProductionConnection(tenantId, claim.ConnectionId, cancellationToken)) return ServiceResult<ReturnDetailView>.Fail("ENVIRONMENT_INVALID", "İade işlemi yalnız test ya da canlı mağaza bağlantısında yapılabilir.", 422);
        if (!stage && !await WritesEnabled(tenantId, claim.ConnectionId, cancellationToken)) return ServiceResult<ReturnDetailView>.Fail("EXTERNAL_WRITES_DISABLED", "Global veya connection dış yazma anahtarı kapalı.", 422);
        if (isHepsiburada && !stage && !await Supported(tenantId, claim.ConnectionId, MarketplaceCapabilities.ReturnWrite, cancellationToken)) return ServiceResult<ReturnDetailView>.Fail("CAPABILITY_EVIDENCE_REQUIRED", "Hepsiburada iade talebine yanıt bu mağaza için henüz doğrulanmadı; yanıt gönderilmedi.", 422);
        if (!await ExternalWritePolicyEnabledAsync(tenantId, claim.ConnectionId, MarketplaceExternalWritePolicies.Return, cancellationToken)) return ServiceResult<ReturnDetailView>.Fail("EXTERNAL_WRITE_POLICY_DISABLED", "İade dış yazma akışı kapalı.", 422);

        // "Stoğa ekle": every approved line must already map to a local variant with a MAIN inventory item,
        // otherwise nothing is sent to the marketplace and nothing is half-done.
        var stockTargets = new List<(ReturnLine Line, InventoryItem Item)>();
        // Stock is chosen per product; the older all-or-nothing flag still means every approved line.
        var stockPlan = new Dictionary<Guid, decimal?>();
        foreach (var requested in command.StockLines ?? [])
        {
            if (requested.Quantity <= 0) return Invalid<ReturnDetailView>("stockLines", "Stoğa eklenecek adet sıfırdan büyük olmalıdır.");
            stockPlan[requested.ReturnLineId] = requested.Quantity;
        }
        foreach (var wholeLineId in command.StockLineIds ?? (command.AddToStock ? returnLineIds : []))
            stockPlan.TryAdd(wholeLineId, null);
        var stockLineIds = stockPlan.Keys.ToArray();
        if (stockLineIds.Length > 0)
        {
            if (action != "APPROVE") return Invalid<ReturnDetailView>("addToStock", "Stoğa ekleme yalnız iade kabulünde seçilebilir.");
            if (stockLineIds.Except(returnLineIds).Any()) return Invalid<ReturnDetailView>("stockLineIds", "Stoğa eklenecek ürünler onaylanan ürünler arasında olmalıdır.");
            var approvedLines = await db.ReturnLines.Where(x => x.TenantId == tenantId && x.ClaimId == claimId && stockLineIds.Contains(x.Id)).ToListAsync(cancellationToken);
            foreach (var approvedLine in approvedLines)
            {
                var variantId = await db.OrderLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == approvedLine.OrderLineId).Select(x => x.VariantId).SingleOrDefaultAsync(cancellationToken);
                // Only checked here, not tracked: the restock after the marketplace call reads the stock row afresh, so a sale
                // meanwhile is not a stale version (SIPARIS-STOK-5).
                var inventoryItem = variantId is null ? null : await db.InventoryItems.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.VariantId == variantId && x.LocationCode == "MAIN", cancellationToken);
                if (inventoryItem is null) return ServiceResult<ReturnDetailView>.Fail("INVENTORY_MAPPING_REQUIRED", "Seçilen ürünlerden biri yerel varyant ve stok kaydıyla eşleşmiyor; stoğa eklenemez.", 422);
                if (stockPlan[approvedLine.Id] is { } wantedUnits && wantedUnits > approvedLine.Quantity) return Invalid<ReturnDetailView>("stockLines", "Stoğa eklenecek adet iade satırı adedini aşamaz.");
                stockTargets.Add((approvedLine, inventoryItem));
            }
        }

        var now = timeProvider.GetUtcNow();
        var decision = new ReturnDecision
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            ClaimId = claimId,
            Action = action,
            ReasonCode = isHepsiburada && action == "APPROVE" ? finalizedWith : string.IsNullOrWhiteSpace(command.ReasonCode) ? null : command.ReasonCode.Trim(),
            Explanation = string.IsNullOrWhiteSpace(command.Explanation) ? null : command.Explanation.Trim(),
            IdempotencyKey = normalizedKey,
            Status = "PENDING",
            ActorUserId = userId,
            CreatedAt = now,
            ReturnLineIds = returnLineIds
        };
        // The stock choice is kept with the decision before anything is sent, so an approval whose answer is unclear
        // keeps it until a later read of the claim settles it (ReturnPendingStock).
        if (stockTargets.Count > 0)
        {
            decision.StockLinesJson = ReturnPendingStock.Serialize(stockTargets.Select(target => new ReturnPendingStockLine(target.Line.Id, stockPlan[target.Line.Id])).ToList());
            decision.StockStatus = ReturnPendingStockPolicy.Pending;
        }
        db.ReturnDecisions.Add(decision);

        var evidenceFiles = new List<ReturnEvidenceFile>(); long totalBytes = 0;
        if (command.EvidenceAssetIds is not null) foreach (var assetId in command.EvidenceAssetIds.Distinct())
        {
            var asset = await db.FileAssets.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == assetId && x.ArchivedAt == null && x.Status == "ACTIVE", cancellationToken);
            if (asset is null) return ServiceResult<ReturnDetailView>.Fail("EVIDENCE_NOT_FOUND", "İade kanıt dosyası bulunamadı; dosyayı yeniden yükleyin.", 422);
            if (asset.Classification != "RETURN_EVIDENCE" || asset.SizeBytes is <= 0 or > 10 * 1024 * 1024 || totalBytes + asset.SizeBytes > 25 * 1024 * 1024 || asset.MimeType is not ("application/pdf" or "image/jpeg" or "image/png")) return ServiceResult<ReturnDetailView>.Fail("EVIDENCE_INVALID", "İade kanıtları PDF/JPEG/PNG olmalı, dosya başına 10 MiB ve toplamda 25 MiB sınırını aşmamalıdır.", 422);
            await using var source = await files.OpenReadAsync(tenantId, asset.RelativePath, cancellationToken);
            await using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, cancellationToken);
            if (buffer.Length != asset.SizeBytes) return ServiceResult<ReturnDetailView>.Fail("RETURN_EVIDENCE_SIZE_MISMATCH", "İade kanıt dosyasının kayıtlı boyutu ile okunan içerik eşleşmedi.", 422);
            var bytes = buffer.ToArray();
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), asset.Sha256, StringComparison.OrdinalIgnoreCase)) return ServiceResult<ReturnDetailView>.Fail("RETURN_EVIDENCE_CHECKSUM_MISMATCH", "İade kanıt dosyasının checksum doğrulaması başarısız oldu.", 422);
            db.ReturnEvidence.Add(new ReturnEvidence { Id = Guid.CreateVersion7(), TenantId = tenantId, ClaimId = claimId, DecisionId = decision.Id, FileAssetId = asset.Id, EvidenceKind = asset.Classification, Checksum = asset.Sha256, CreatedAt = now });
            evidenceFiles.Add(new(asset.OriginalNameSafe ?? $"evidence-{asset.Id:N}", asset.MimeType, bytes));
            totalBytes += bytes.LongLength;
        }

        var effect = new ExternalEffectRecord { Id = Guid.CreateVersion7(), TenantId = tenantId, EffectType = effectType, IdempotencyKey = normalizedKey, CreatedAt = now, ConnectionId = claim.ConnectionId, TargetId = claim.Id, Action = $"RETURN_{action}" };
        db.ExternalEffectRecords.Add(effect);
        if (await ReserveReturnDecisionAsync(tenantId, claimId, decision, isHepsiburada, cancellationToken) is { } reservationError) return reservationError;

        var lineIds = await db.ReturnLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.ClaimId == claimId && returnLineIds.Contains(x.Id)).OrderBy(x => x.Id).Select(x => x.ExternalLineId).ToListAsync(cancellationToken);
        var context = new AdapterContext(tenantId, claim.ConnectionId, correlationId, normalizedKey, now.AddSeconds(30));
        AdapterResult<ReturnActionResult> result;
        try
        {
            result = await returns.ExecuteAsync(context, new(claim.ExternalClaimId, lineIds, action, action == "APPROVE" && isHepsiburada ? null : decision.ReasonCode, decision.Explanation, evidenceFiles, isHepsiburada ? finalizedWith : null,
                action == "REJECT" && isN11 ? command.ReturnShipment : null), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The call may or may not have reached the marketplace: the reserved decision is not left PENDING but
            // waits for the effect check, like an unclear answer, and the claim stays held until it settles.
            decision.Status = "MANUAL_REVIEW";
            decision.ErrorCode = "EXTERNAL_EFFECT_VERIFYING";
            decision.CompletedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(CancellationToken.None);
            return await VerifyLaterAsync<ReturnDetailView>(effect, CancellationToken.None);
        }
        if (!result.IsSuccess)
        {
            var error = result.Error;
            decision.ErrorCode = error?.Code ?? "RETURN_ACTION_FAILED";
            decision.ExternalOperationId ??= error?.RemoteRequestId;
            decision.CompletedAt = timeProvider.GetUtcNow();
            if (error is null || IsAmbiguous(error))
            {
                // The outcome is settled by the effect check or the next claim
                // read; the code marks it as waiting for that, not as a conflict.
                decision.Status = "MANUAL_REVIEW";
                decision.ErrorCode = "EXTERNAL_EFFECT_VERIFYING";
                await db.SaveChangesAsync(cancellationToken);
                return await VerifyLaterAsync<ReturnDetailView>(effect, cancellationToken);
            }
            db.ExternalEffectRecords.Remove(effect);
            decision.Status = "FAILED";
            ReturnPendingStock.Drop(decision, decision.CompletedAt.Value);
            await db.SaveChangesAsync(cancellationToken);
            return ServiceResult<ReturnDetailView>.Fail(error.Code, error.SafeMessage, error.HttpStatus is >= 400 and <= 599 ? error.HttpStatus.Value : 502);
        }

        decision.Status = "SUBMITTED";
        decision.ExternalOperationId = result.Value?.ExternalOperationId;
        decision.ErrorCode = null;
        effect.CompletedAt = timeProvider.GetUtcNow();
        // The accepted write is recorded first, on its own: a conflict in the claim or stock save below must not turn a
        // decision the marketplace already took into a server error (SIPARIS-STOK-5).
        await db.SaveChangesAsync(cancellationToken);
        var completedAt = effect.CompletedAt.Value;
        // The claim and the restock are written on fresh values; a sale or a claim read that changes the same rows in
        // between runs the attempt again. When it keeps colliding, the decision stays SUBMITTED with its pending stock
        // choice, which the next read of the claim settles (ReturnPendingStock.SettleClaimAsync).
        var settled = await SerializableWriteRetry.RunAsync(db, async token =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
            await db.Entry(claim).ReloadAsync(token);
            await db.Entry(decision).ReloadAsync(token);
            // The claim's lines decided before this one count: rejecting the last waiting lines of a claim whose other lines
            // were approved leaves it approved in part ("Kısmen Onaylandı"), so those lines still reach the invoice.
            claim.Status = ReturnClaimDecisionPolicy.StatusAfterLineDecision(claim.Status, action, returnLineIds.Length, actionableLineIds.Count,
                claimLines.Where(line => !returnLineIds.Contains(line.Id)).Select(line => line.RawStatus).ToList());
            if (!isHepsiburada && action is "APPROVE" or "REJECT")
            {
                var settledStatus = action == "APPROVE" ? "Accepted" : "Rejected";
                foreach (var settledLine in await db.ReturnLines.Where(x => x.TenantId == tenantId && x.ClaimId == claimId && returnLineIds.Contains(x.Id)).ToListAsync(token)) settledLine.RawStatus = settledStatus;
            }
            claim.RawStatus = partialLineDecision ? "PARTIAL_LINE_DECISION_PENDING" : result.Value?.Status ?? (action switch
            {
                "APPROVE" => "ACCEPTED",
                "REJECT" => "REJECTED",
                _ => "PREAPPROVAL_CONFIRM_SUBMITTED"
            });
            // The marketplace accepted the approval: the chosen lines go back to stock now, once.
            claim.UpdatedAt = completedAt;
            claim.Version++;
            await ReturnPendingStock.SettleAsync(db, decision, force: action == "APPROVE", correlationId, completedAt, token);
            await db.SaveChangesAsync(token);
            await transaction.CommitAsync(token);
            return true;
        }, () => false, cancellationToken);
        if (!settled)
        {
            db.ChangeTracker.Clear();
            return await ReturnAsync(tenantId, claimId, cancellationToken);
        }

        // The action itself is already complete. Read-back is best-effort and
        // only refines the local status; it must not put the user action in a
        // background queue or hide the provider response from the panel.
        var readback = await returns.GetAsync(new AdapterContext(tenantId, claim.ConnectionId, correlationId, $"{normalizedKey}:readback", timeProvider.GetUtcNow().AddSeconds(30)), claim.ExternalClaimId, cancellationToken);
        if (readback.IsSuccess && readback.Value is not null)
        {
            var remoteStatus = MarketplaceReturnStatus.Canonicalize(readback.Value.RawStatus, readback.Value.CargoTrackingLink);
            if (!ReturnClaimStoragePolicy.ShouldPersist(remoteStatus))
            {
                // The decision just made is history: the claim is kept as
                // cancelled with it instead of being deleted.
                if (await CancelledReturnClaimCleanup.RemoveOrRetainAsync(db, tenantId, claimId, readback.Value.RawStatus, readback.Value.LastModifiedAt, timeProvider.GetUtcNow(), cancellationToken))
                {
                    decision.Status = "MANUAL_REVIEW";
                    decision.ErrorCode = "RETURN_ACTION_READBACK_CONFLICT";
                    decision.CompletedAt = timeProvider.GetUtcNow();
                    await db.SaveChangesAsync(cancellationToken);
                    return ServiceResult<ReturnDetailView>.Fail("RETURN_CLAIM_CANCELLED", "Pazaryerindeki iade talebi iptal edilmiş; iade, verilen kararla birlikte iptal olarak saklandı.", 409);
                }
                db.ChangeTracker.Clear();
                return ServiceResult<ReturnDetailView>.Fail("RETURN_CLAIM_CANCELLED", "Pazaryerindeki iade talebi iptal edilmiş; iptal kaydı saklanmadı.", 409);
            }
            // The lines decide a claim decided both ways (approved in part), as on the scheduled read (UpsertReturn).
            var remoteClaimStatus = ReturnLineStatusPolicy.Resolve(remoteStatus, readback.Value.Lines.Select(remoteLine => remoteLine.RawStatus).ToList());
            if (!partialLineDecision && ReturnClaimStateMachine.CanTransition(claim.Status, remoteClaimStatus)) claim.Status = remoteClaimStatus;
            claim.RawStatus = partialLineDecision ? "PARTIAL_LINE_DECISION_PENDING" : readback.Value.RawStatus;
            claim.CargoProviderName = readback.Value.CargoProviderName ?? claim.CargoProviderName;
            claim.CargoTrackingNumber = readback.Value.CargoTrackingNumber ?? claim.CargoTrackingNumber;
            claim.ReasonCode = readback.Value.ReasonCode;
            claim.ReasonText = readback.Value.ReasonText;
            claim.ActionDueAt = readback.Value.ActionDueAt;
            claim.LastRemoteModifiedAt = readback.Value.LastModifiedAt ?? claim.LastRemoteModifiedAt;
            claim.UpdatedAt = timeProvider.GetUtcNow();
            claim.Version++;
            // The decision is judged by its own lines: Trendyol's claim status is the first item's, which may be a line
            // this decision did not cover and that still waits for its own decision.
            var decisionStatus = ReturnDecisionLinePolicy.Outcome(remoteStatus,
                lineIds.Select(lineId => readback.Value.Lines.FirstOrDefault(remoteLine => remoteLine.ExternalLineId == lineId)?.RawStatus).ToList());
            var confirmed = action == "APPROVE" && decisionStatus is ReturnClaimStatus.Approved or ReturnClaimStatus.Completed
                || action == "REJECT" && decisionStatus is ReturnClaimStatus.Rejected or ReturnClaimStatus.Disputed
                || preApprovalConfirm && (decisionStatus is ReturnClaimStatus.AwaitingShipment or ReturnClaimStatus.InTransit
                    || decisionStatus == ReturnClaimStatus.ActionRequired && !HepsiburadaReturnActionPolicy.IsAwaitingPreApproval(readback.Value.RawStatus));
            var conflicting = action == "APPROVE" && decisionStatus is ReturnClaimStatus.Rejected or ReturnClaimStatus.Cancelled
                || action == "REJECT" && decisionStatus is ReturnClaimStatus.Approved or ReturnClaimStatus.Completed
                || preApprovalConfirm && decisionStatus is ReturnClaimStatus.Approved or ReturnClaimStatus.Rejected or ReturnClaimStatus.Cancelled;
            if (confirmed) { decision.Status = "SUCCEEDED"; decision.CompletedAt = claim.UpdatedAt; }
            else if (conflicting) { decision.Status = "MANUAL_REVIEW"; decision.ErrorCode = "RETURN_ACTION_READBACK_CONFLICT"; decision.CompletedAt = claim.UpdatedAt; }
        }
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The read-back only refines a decision already recorded; a claim read that saved meanwhile keeps its own.
            db.ChangeTracker.Clear();
        }
        return await ReturnAsync(tenantId, claimId, cancellationToken);
    }

    // Reserves a return decision before the marketplace is called (RET-02). In one short transaction the claim's
    // version moves on only while it is still the one this request read (Version is the claim's concurrency token, so
    // the UPDATE is conditional and holds the row), the open decisions are read again under that lock, and the PENDING
    // decision commits with its effect record. Of two requests that read the same claim only one is reserved; the other
    // calls nothing and gets a conflict. Null when reserved.
    private async Task<ServiceResult<ReturnDetailView>?> ReserveReturnDecisionAsync(Guid tenantId, Guid claimId, ReturnDecision decision, bool sentDecisionHoldsClaim, CancellationToken cancellationToken)
    {
        var claim = db.ReturnClaims.Local.Single(x => x.TenantId == tenantId && x.Id == claimId);
        var lineIds = decision.ReturnLineIds ?? [];
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            try
            {
                claim.Version++;
                claim.UpdatedAt = decision.CreatedAt;
                await db.SaveChangesAsync(cancellationToken);
                var openLines = await OpenReturnDecisions(tenantId, claimId, sentDecisionHoldsClaim).Where(x => x.Id != decision.Id).Select(x => x.ReturnLineIds).ToListAsync(cancellationToken);
                if (!openLines.Any(open => ReturnDecisionLinePolicy.Overlaps(open, lineIds)))
                {
                    await transaction.CommitAsync(cancellationToken);
                    return null;
                }
            }
            catch (DbUpdateException)
            {
                // Another request moved the claim on first (or reserved the same Idempotency-Key): answered below.
            }
            await transaction.RollbackAsync(CancellationToken.None);
        }
        db.ChangeTracker.Clear();
        var prior = await db.ReturnDecisions.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.IdempotencyKey == decision.IdempotencyKey, cancellationToken);
        if (prior is not null && prior.ClaimId == claimId && prior.Status is "SUCCEEDED" or "SUBMITTED") return await ReturnAsync(tenantId, claimId, cancellationToken);
        var stillOpen = await OpenReturnDecisions(tenantId, claimId, sentDecisionHoldsClaim).Select(x => x.ReturnLineIds).ToListAsync(cancellationToken);
        if (prior is not null || stillOpen.Any(open => ReturnDecisionLinePolicy.Overlaps(open, lineIds)))
            return ServiceResult<ReturnDetailView>.Fail("RETURN_DECISION_IN_PROGRESS", "Bu iade için başka bir karar az önce pazaryerine gönderildi; sonuç kesinleşene kadar yeni bir karar gönderilemez.", 409);
        var currentVersion = await db.ReturnClaims.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == claimId).Select(x => x.Version).FirstOrDefaultAsync(cancellationToken);
        return ServiceResult<ReturnDetailView>.Fail("CONCURRENCY_CONFLICT", "İade siz düzenlerken başka bir işlemle güncellendi. Sayfayı yenileyip tekrar deneyin.", 409);
    }

    private static readonly IReadOnlyList<string> ReturnActions = ["APPROVE", "REJECT"];

    // Return decisions whose outcome is still open: waiting to be sent or verified, or sent while the request's effect
    // is not settled. A sent decision on line-level marketplaces is closed once the marketplace accepted it (the
    // decided lines are no longer actionable); Hepsiburada decides the whole claim and its sent decision holds the
    // claim until the claim read confirms it, as before.
    private IQueryable<ReturnDecision> OpenReturnDecisions(Guid tenantId, Guid claimId, bool sentDecisionHoldsClaim) => db.ReturnDecisions.AsNoTracking()
        .Where(x => x.TenantId == tenantId && x.ClaimId == claimId
            && (x.Status == "PENDING" || x.Status == "RETRY_SCHEDULED" || x.Status == "MANUAL_REVIEW"
                || x.Status == "SUBMITTED" && db.ExternalEffectRecords.Any(effect => effect.TenantId == x.TenantId && effect.IdempotencyKey == x.IdempotencyKey && (sentDecisionHoldsClaim || effect.CompletedAt == null))));
}
