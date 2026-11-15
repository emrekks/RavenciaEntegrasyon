using System.Data;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.Configuration;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed partial class MarketplaceSalesService(AppDbContext db, CursorCodec cursors, IConfiguration configuration, IProductVisualLookupPort productVisuals, IOrderPort orders, IReturnPort returns, IPrivateFileStorage files, TimeProvider timeProvider) : IMarketplaceSalesService
{
    // A real Trendyol package supersedes the zero-ID payment placeholder, and a
    // package another package names as its origin (a split UnPacked parent, a
    // resent original) is superseded by it. Retain their history without
    // displaying, counting or invoicing them as live packages.
    internal IQueryable<ShipmentPackage> CurrentShipmentPackages() => CurrentShipmentPackages(db);

    // Also used by the dashboard and invoicing, so their package sets follow the same rule as the order screens.
    internal static IQueryable<ShipmentPackage> CurrentShipmentPackages(AppDbContext db) => CurrentShipmentPackages(db, db.ShipmentPackages.AsNoTracking());

    // The same rule over a caller's source, e.g. tracked packages that the invoicing precheck updates.
    internal static IQueryable<ShipmentPackage> CurrentShipmentPackages(AppDbContext db, IQueryable<ShipmentPackage> packages) => packages
        .Where(package => package.RawStatus != ShipmentPackageSupersession.LocallyUnpackedRawStatus)
        .Where(package => (package.ExternalPackageId != "0"
            || !db.PlatformConnections.Any(connection => connection.TenantId == package.TenantId
                && connection.Id == package.ConnectionId && connection.PlatformCode == "TRENDYOL")
            || !db.ShipmentPackages.Any(real => real.TenantId == package.TenantId
                && real.ConnectionId == package.ConnectionId && real.OrderId == package.OrderId
                && real.ExternalPackageId != "0" && real.ExternalPackageId != ""))
            && !db.ShipmentPackages.Any(child => child.TenantId == package.TenantId
                && child.ConnectionId == package.ConnectionId && child.OrderId == package.OrderId
                && child.OriginExternalPackageId == package.ExternalPackageId
                && child.ExternalPackageId != package.ExternalPackageId));

    // The shared catalog key (CatalogKeyNormalizer), so order and return lines find variants stored by the panel
    // or the import under the same key (K1).
    private static string NormalizeCatalogKey(string? value, int maximum) => CatalogKeyNormalizer.Key(value, maximum);

    // The codes an order or return line may name a linked variant by: the link id and the marketplace's stock code
    // (a Hepsiburada link is keyed by its hbSku and keeps the merchant SKU beside it).
    internal static IEnumerable<string> LinkIdentities(MarketplaceVariantLink link)
    {
        yield return MarketplaceVariantLinkCoverage.Normalize(link.ExternalId);
        if (!string.IsNullOrWhiteSpace(link.ExternalSku)) yield return MarketplaceVariantLinkCoverage.Normalize(link.ExternalSku);
    }

    // The marketplace's own ids for the sold item in a line snapshot (Trendyol productCode/contentId, Hepsiburada
    // hbSku/listingId, Shopify variant_id), which a variant link is keyed by. They still find the linked variant after
    // the barcode or stock code was changed on one side only: Trendyol line MZ052BOD-16 is listing 1696621714, linked
    // to the panel variant still coded MZ051BYC-16, and matched no variant by its codes.
    private static readonly string[] SourceListingIdFields = ["productCode", "contentId", "hbSku", "listingId", "variantId", "variant_id"];

    internal static string[] LineMarketplaceIdentities(string? sku, string? sourceSnapshotJson)
    {
        var identities = new List<string>();
        var normalizedSku = MarketplaceVariantLinkCoverage.Normalize(sku);
        if (normalizedSku.Length > 0) identities.Add(normalizedSku);
        if (!string.IsNullOrWhiteSpace(sourceSnapshotJson))
        {
            try
            {
                using var document = JsonDocument.Parse(sourceSnapshotJson);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                    foreach (var field in SourceListingIdFields)
                    {
                        if (!document.RootElement.TryGetProperty(field, out var value)) continue;
                        var text = value.ValueKind switch { JsonValueKind.String => value.GetString(), JsonValueKind.Number => value.GetRawText(), _ => null };
                        var normalized = MarketplaceVariantLinkCoverage.Normalize(text);
                        if (normalized.Length > 0 && normalized != "0" && !identities.Contains(normalized)) identities.Add(normalized);
                    }
            }
            catch (JsonException) { }
        }
        return identities.ToArray();
    }

    internal static string[] LineMarketplaceIdentities(OrderLine line) => LineMarketplaceIdentities(line.Sku, line.SourceSnapshotJson);

    internal static string[] CatalogLookupKeys(string? value, int maximum = 160)
    {
        var normalized = NormalizeCatalogKey(value, maximum);
        if (normalized.Length == 0) return [];
        // Stored variant keys may still be in a legacy form until the deferred recompute; look up those too.
        var keys = CatalogKeyNormalizer.LookupKeys(value, maximum).ToList();
        var withoutMarketplacePadding = normalized.TrimStart('0');
        if (withoutMarketplacePadding.Length > 0
            && withoutMarketplacePadding.Length < normalized.Length
            && withoutMarketplacePadding.Any(char.IsLetter))
            keys.Add(withoutMarketplacePadding);
        return keys.ToArray();
    }

    internal static Dictionary<string, ProductVariant> BarcodeVariantLookup(IEnumerable<ProductVariant> variants) => variants
        .Where(variant => !string.IsNullOrWhiteSpace(variant.Barcode))
        .SelectMany(variant => CatalogLookupKeys(variant.Barcode).Select(key => (Key: key, Variant: variant)))
        .GroupBy(candidate => candidate.Key, StringComparer.OrdinalIgnoreCase)
        .Where(group => group.Select(candidate => candidate.Variant.Id).Distinct().Count() == 1)
        .ToDictionary(group => group.Key, group => group.First().Variant, StringComparer.OrdinalIgnoreCase);

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

    public async Task<ServiceResult<ReturnDetailView>> ApplyDispositionAsync(Guid tenantId, Guid userId, Guid claimId, ReturnDispositionCommand command, string idempotencyKey, string correlationId, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<ReturnStockDispositionKind>(command.Disposition, true, out var disposition)) return Invalid<ReturnDetailView>("disposition", "Disposition PASS, QUARANTINE, DAMAGED veya NOT_RECEIVED olmalıdır.");
        if (command.Quantity <= 0 || string.IsNullOrWhiteSpace(command.Reason)) return Invalid<ReturnDetailView>("quantity", "Pozitif quantity ve açıklama zorunludur.");

        // The return line is locked before anything is read, and held until the disposition commits: two operators,
        // or an operator and the automatic "Stoğa Ekle" settlement (ReturnPendingStock, which takes the same lock),
        // put the line's units somewhere one after the other, each reading what the other already did (YENİ-04).
        // Read committed, so the read after the lock sees the other's committed rows; a serializable snapshot taken
        // before the wait would not.
        // The inventory item carries a version: a sale or another stock change on the same item between the read and the
        // save is a concurrency conflict. The whole attempt (lock, reads, the delta on the fresh on-hand) runs again, a few
        // times, and a conflict that keeps coming back is a 409 STOCK_CHANGED instead of a server error (scan 13 Nov 2026).
        var outcome = await SerializableWriteRetry.RunAsync<ServiceResult<ReturnDetailView>?>(db, async token =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
            try
            {
                await ReturnPendingStock.LockLinesAsync(db, tenantId, [command.ReturnLineId], token);
                var existing = await db.ReturnStockDispositions.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.IdempotencyKey == idempotencyKey, token);
                if (existing)
                {
                    await transaction.CommitAsync(token);
                    return null;
                }

                var claim = await db.ReturnClaims.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == claimId, token);
                if (claim is null) return NotFound<ReturnDetailView>();
                var line = await db.ReturnLines.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ClaimId == claimId && x.Id == command.ReturnLineId, token);
                if (line is null) return NotFound<ReturnDetailView>();
                // Decided per line: a line approved on its own takes stock while the claim's other lines still wait.
                if (!ReturnDecisionLinePolicy.StockDispositionAllowed(claim.Status, line.RawStatus)) return ServiceResult<ReturnDetailView>.Fail("RETURN_DISPOSITION_NOT_ALLOWED", "Stok kararı yalnız onaylanan iade ürünlerine uygulanabilir.", 409);
                var already = await ReturnPendingStock.DisposedQuantityAsync(db, tenantId, line.Id, token);
                if (already + command.Quantity > line.Quantity) return ServiceResult<ReturnDetailView>.Fail("RETURN_QUANTITY_EXCEEDED", "Disposition toplamı iade satırı miktarını aşamaz.", 409);
                var variantId = await db.OrderLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == line.OrderLineId).Select(x => x.VariantId).SingleAsync(token);
                if (variantId is null) return ServiceResult<ReturnDetailView>.Fail("INVENTORY_MAPPING_REQUIRED", "İade satırı yerel varyantla eşleşmiyor.", 422);
                var item = await db.InventoryItems.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.VariantId == variantId && x.LocationCode == "MAIN", token);
                if (item is null) return ServiceResult<ReturnDetailView>.Fail("INVENTORY_MAPPING_REQUIRED", "MAIN inventory item bulunamadı.", 422);
                // SIPARIS-STOK-6: with the other claims of the same order line, never more back in stock than was sold.
                if (disposition == ReturnStockDispositionKind.Pass
                    && await StockMovementWriter.ReturnRestockRoomAsync(db, tenantId, line.OrderLineId, token) is var room && command.Quantity > room.Left)
                    return ServiceResult<ReturnDetailView>.Fail("RETURN_ORDER_LINE_QUANTITY_EXCEEDED", $"Bu sipariş satırından {StockMovementWriter.Units(room.Sold)} adet satıldı ve iade taleplerinden {StockMovementWriter.Units(room.Restocked)} adedi zaten stoğa eklendi; en fazla {StockMovementWriter.Units(room.Left)} adet daha stoğa eklenebilir.", 409);
                var now = timeProvider.GetUtcNow();
                db.ReturnStockDispositions.Add(new ReturnStockDisposition { Id = Guid.CreateVersion7(), TenantId = tenantId, ClaimId = claimId, ReturnLineId = line.Id, InventoryItemId = item.Id, Disposition = disposition, Quantity = command.Quantity, IdempotencyKey = idempotencyKey, Reason = command.Reason.Trim(), ActorUserId = userId, CreatedAt = now });
                if (disposition == ReturnStockDispositionKind.Pass)
                {
                    var change = StockMovementWriter.Move(db, item, command.Quantity, StockMovementRounding.None, ReturnPendingStock.RestockSource(claim, idempotencyKey, userId, correlationId, now));
                    await StockProjectionOutbox.EnqueueAsync(db, tenantId, [change], now, token);
                }
                db.AuditLogs.Add(new AuditLog { TenantId = tenantId, ActorUserId = userId, Action = "RETURN_STOCK_DISPOSITION", TargetType = "ReturnClaim", TargetId = claimId.ToString("D"), Reason = command.Reason.Trim(), CorrelationId = correlationId, CreatedAt = now });
                await db.SaveChangesAsync(token);
                await transaction.CommitAsync(token);
            }
            catch (DbUpdateException exception) when (!SerializableWriteRetry.IsRetryable(exception))
            {
                await transaction.RollbackAsync(CancellationToken.None);
                // A replay that committed meanwhile (the same key) is the same disposition.
                if (await db.ReturnStockDispositions.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.IdempotencyKey == idempotencyKey, token)) return null;
                throw;
            }
            return null;
        }, () => ServiceResult<ReturnDetailView>.Fail("STOCK_CHANGED", "Stok aynı anda başka bir işlemle (ör. yeni sipariş) değiştiği için kaydedilemedi. Sayfayı yenileyip tekrar deneyin.", 409), cancellationToken);
        return outcome ?? await ReturnAsync(tenantId, claimId, cancellationToken);
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

    private Task<bool> IsStageConnection(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => db.PlatformConnections.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.Id == connectionId && (x.Status == "ACTIVE" || x.Status == "VERIFIED") && x.Environment == "STAGE", cancellationToken);
    private Task<bool> IsProductionConnection(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) => db.PlatformConnections.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.Id == connectionId && (x.Status == "ACTIVE" || x.Status == "VERIFIED") && x.Environment == "PRODUCTION", cancellationToken);
    private async Task<bool> WritesEnabled(Guid tenantId, Guid connectionId, CancellationToken cancellationToken) { if (!configuration.GetValue<bool>("FeatureFlags:ExternalWrites")) return false; var settings = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId).Select(x => x.SettingsJson).SingleOrDefaultAsync(cancellationToken); if (settings is null) return false; try { using var document = JsonDocument.Parse(settings); return document.RootElement.TryGetProperty("ExternalWritesEnabled", out var value) && value.ValueKind == JsonValueKind.True; } catch (JsonException) { return false; } }
    private async Task<bool> ExternalWritePolicyEnabledAsync(Guid tenantId, Guid connectionId, string resourceType, CancellationToken cancellationToken) => (await ExternalWritePolicyAsync(tenantId, connectionId, resourceType, cancellationToken)).Enabled;
    private async Task<(bool Enabled, int IntervalSeconds)> ExternalWritePolicyAsync(Guid tenantId, Guid connectionId, string resourceType, CancellationToken cancellationToken)
    {
        var policy = await db.ConnectionSyncPolicies.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == resourceType, cancellationToken);
        return policy is null ? (false, 0) : (policy.Enabled, Math.Clamp(policy.IntervalSeconds, 0, 86_400));
    }
    private async Task<IReadOnlyList<string>> CapabilityValues(Guid tenantId, Guid connectionId, string code, string property, CancellationToken cancellationToken) { var capability = await db.PlatformCapabilities.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Code == code && x.SupportLevel == CapabilitySupportLevel.Supported, cancellationToken); if (capability?.ConstraintsJson is null) return []; try { using var doc = JsonDocument.Parse(capability.ConstraintsJson); return doc.RootElement.TryGetProperty(property, out var values) && values.ValueKind == JsonValueKind.Array ? values.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : []; } catch (JsonException) { return []; } }
    private Task<Dictionary<Guid, string>> MediaUrls(Guid tenantId, IEnumerable<Guid?> variantIds, CancellationToken cancellationToken) =>
        OrderLineCatalog.MediaUrlsAsync(db, tenantId, variantIds.Where(x => x is not null).Select(x => x!.Value), cancellationToken);

    // The marketplace's own photo address in a line snapshot, ready to show; null when the line carries none.
    internal static string? SourceLineImageUrl(string? json) => NormalizeImageUrl(SourceImageUrl(json ?? "{}"));

    // The order's buyer kind by the invoice's rule (report item 12), and the order type that follows from it: a micro
    // export first, then KURUMSAL for a corporate buyer (with or without its tax number), else BIREYSEL. The kind stored
    // on the order (Order.BuyerType) decides when set, so the label always equals what the "Fatura Türü" filter matched.
    internal static (OrderBuyerTypeView BuyerType, string OrderType) BuyerType(string customerJson, string invoiceAddressJson, bool isMicroExport, string? storedKind = null)
    {
        var buyer = OrderBuyerTypes.Label(storedKind, customerJson, invoiceAddressJson);
        return (buyer, isMicroExport ? "MIKRO_IHRACAT" : buyer.Kind == OrderBuyerTypeView.Individual ? "BIREYSEL" : "KURUMSAL");
    }

    private static (string Name, string? Email, string? Phone, string? TaxOrIdentityNumber, bool IsMicroExport, bool? IsEInvoiceAvailable) Customer(string customerJson, string invoiceAddressJson, string shipmentAddressJson)
    {
        var name = ResolveCustomerName(customerJson, invoiceAddressJson, shipmentAddressJson);
        var email = JsonText(customerJson, "customerEmail", "email") ?? JsonText(invoiceAddressJson, "email") ?? JsonText(shipmentAddressJson, "email");
        var phone = JsonText(customerJson, "customerPhone", "customerPhoneNumber", "phone", "phoneNumber") ?? JsonText(invoiceAddressJson, "phone", "phoneNumber", "mobilePhone") ?? JsonText(shipmentAddressJson, "phone", "phoneNumber", "mobilePhone");
        var tax = ResolveCustomerTaxOrIdentityNumber(customerJson, invoiceAddressJson, shipmentAddressJson);
        var microText = JsonText(customerJson, "shipmentPackageType", "orderType");
        // Trendyol exports through its 3P partner model explicitly return micro=false. The documented
        // 3pByTrendyol=true signal is still an export order and must be presented as such to operators.
        // Historical Stage snapshots may predate the export flags while retaining Trendyol's
        // documented PM3/Arvato export-partner identity. Keep that narrow legacy signal so
        // existing export orders are not presented as domestic orders.
        var legacyExportPartner = IsLegacyTrendyolExportPartner(name);
        var micro = JsonBool(customerJson, "micro", "microExport", "3pByTrendyol") || microText?.Contains("MICRO", StringComparison.OrdinalIgnoreCase) == true || microText?.Contains("İHRAC", StringComparison.OrdinalIgnoreCase) == true || legacyExportPartner;
        var eInvoice = JsonNullableBool(customerJson, "eInvoiceAvailable", "isEInvoice") ?? JsonNullableBool(invoiceAddressJson, "eInvoiceAvailable", "isEInvoice");
        return (name, email, phone, tax, micro, eInvoice);
    }

    private static readonly HashSet<string> FastDeliveryTypes = new(["TodayDelivery", "SameDayShipping", "FastDelivery"], StringComparer.OrdinalIgnoreCase);

    // The order row's marks, read from the package fields the marketplace sent (top level of the customer snapshot only,
    // never from a line). A field that is missing or holds another value leaves its mark out.
    // - Micro export: Trendyol micro=true only. micro and countryCode are independent (Trendyol docs), so a foreign
    //   country alone is no micro export. The country is the invoice address's: the shipment address of a micro order
    //   is Trendyol's Istanbul export depot (TR); the storefront is the fallback.
    // - Delivery point: Trendyol deliveryAddressType=CollectionPoint; n11 any deliveryAddressType (KTN, EASYPOINT, PUP).
    // - Fast delivery: Trendyol fastDeliveryType TodayDelivery/SameDayShipping/FastDelivery, unless the same package
    //   says fastDelivery=false (the two disagree; no mark rather than a wrong one).
    internal static OrderRowMarks? RowMarks(string? platformCode, string? storeFrontCode, string customerJson, string invoiceAddressJson)
    {
        var platform = platformCode?.Trim().ToUpperInvariant();
        if (platform is not ("TRENDYOL" or "N11")) return null;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(customerJson) ? "{}" : customerJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string? TopText(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;
            bool? TopBool(string name) => root.TryGetProperty(name, out var value) ? value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;

            OrderRowMarks marks;
            if (platform == "N11")
            {
                var pointType = TopText("deliveryAddressType") is { Length: > 0 } type && !string.Equals(type, "null", StringComparison.OrdinalIgnoreCase) ? type.ToUpperInvariant() : null;
                marks = new(false, null, pointType is not null, pointType, null);
            }
            else
            {
                var micro = TopBool("micro") == true;
                var country = micro ? MicroExportCountry(invoiceAddressJson, storeFrontCode) : null;
                var deliveryPoint = string.Equals(TopText("deliveryAddressType"), "CollectionPoint", StringComparison.OrdinalIgnoreCase);
                var fastType = TopText("fastDeliveryType") is { } fast && FastDeliveryTypes.TryGetValue(fast, out var known) && TopBool("fastDelivery") != false ? known : null;
                marks = new(micro, country, deliveryPoint, null, fastType);
            }
            return marks.MicroExport || marks.DeliveryPoint || marks.FastDeliveryType is not null ? marks : null;
        }
        catch (JsonException) { return null; }
    }

    private static string? MicroExportCountry(string invoiceAddressJson, string? storeFrontCode)
    {
        static string? Code(string? value) => value?.Trim().ToUpperInvariant() is { Length: 2 } code && code.All(char.IsAsciiLetterUpper) && code != "TR" ? code : null;
        string? invoiceCountry = null;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(invoiceAddressJson) ? "{}" : invoiceAddressJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("countryCode", out var value) && value.ValueKind == JsonValueKind.String)
                invoiceCountry = value.GetString();
        }
        catch (JsonException) { }
        return Code(invoiceCountry) ?? Code(storeFrontCode);
    }

    internal static string ResolveCustomerName(string customerJson, string invoiceAddressJson, string shipmentAddressJson)
    {
        var customerName = NameFrom(customerJson, "customerFirstName", "customerLastName", "customerName", "customerFullName", "buyerName", "fullName", "name");
        var invoiceName = NameFrom(invoiceAddressJson, "firstName", "lastName", "invoiceFirstName", "invoiceLastName", "fullName", "name", "company", "companyName");
        var shipmentName = NameFrom(shipmentAddressJson, "firstName", "lastName", "shippingFirstName", "shippingLastName", "fullName", "name", "company", "companyName");
        return FirstMeaningful(customerName, invoiceName, shipmentName) ?? "—";
    }

    internal static string? ResolveCustomerTaxOrIdentityNumber(string customerJson, string invoiceAddressJson, string shipmentAddressJson) =>
        JsonText(customerJson, "customerTaxNumber", "taxNumber", "identityNumber", "customerIdentityNumber", "tcIdentityNumber")
        ?? JsonText(invoiceAddressJson, "taxNumber", "identityNumber", "tcIdentityNumber")
        ?? JsonText(shipmentAddressJson, "taxNumber", "identityNumber", "tcIdentityNumber");

    private static string? NameFrom(string json, string firstName, string lastName, params string[] fullNameFields)
    {
        var first = JsonText(json, firstName);
        var last = JsonText(json, lastName);
        var combined = string.Join(' ', new[] { first, last }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return !string.IsNullOrWhiteSpace(combined) ? combined : JsonText(json, fullNameFields);
    }

    private static string? FirstMeaningful(params string?[] names) => names.FirstOrDefault(name => IsMeaningfulText(name) && !IsPlaceholderCustomerName(name!));

    private static bool IsPlaceholderCustomerName(string name) => name.Trim() is "Adı Soyadı" or "Ad Soyad" or "İsim Soyisim";

    internal static bool IsLegacyTrendyolExportPartner(string name) =>
        name.Contains("PM3", StringComparison.OrdinalIgnoreCase) && name.Contains("ARVATO", StringComparison.OrdinalIgnoreCase);

    // A shipping deadline only: Trendyol's (extended) agreed hand-over date or a marketplace's due date;
    // delivery estimates and modification times are not deadlines.
    private static DateTimeOffset? OperationalDueAt(string json) =>
        JsonInstant(json, "extendedAgreedDeliveryDate", "agreedDeliveryDate", "dueDate", "shipmentDueDate", "deliveryDueAt");

    internal static string InvoiceLabel(Invoice? invoice, string customerJson, bool marketplaceDeliveryConfirmed = false) =>
        InvoiceLabel(invoice, customerJson, [], marketplaceDeliveryConfirmed);

    internal static string InvoiceLabel(Invoice? invoice, string customerJson, IEnumerable<string?> packageRawStatuses, bool marketplaceDeliveryConfirmed = false) =>
        InvoiceLabel(invoice?.Status, invoice?.InvoiceNumber, customerJson, packageRawStatuses, marketplaceDeliveryConfirmed);

    internal static string InvoiceLabel(InvoiceStatus? invoiceStatus, string? invoiceNumber, string customerJson, IEnumerable<string?> packageRawStatuses, bool marketplaceDeliveryConfirmed = false)
    {
        // The local fiscal invoice and the marketplace delivery are separate
        // facts. A fiscal invoice number does not prove that Trendyol accepted
        // the document, so Submitted/Accepted/MarketplacePending stay visible
        // as an in-progress marketplace delivery.
        if (invoiceStatus is { } status)
        {
            // A lost provider answer is not a rejection: the invoice may exist at the provider.
            if (status == InvoiceStatus.UnknownResult) return "FATURA_BELIRSIZ";
            if (status is InvoiceStatus.Rejected or InvoiceStatus.ValidationFailed or InvoiceStatus.ManualReview) return "FATURA_REDDEDILDI";
            if (status == InvoiceStatus.MarketplaceFailed) return "FATURA_PLATFORMA_AKTARILMADI";
            if (status is InvoiceStatus.Accepted or InvoiceStatus.MarketplacePending or InvoiceStatus.Submitted) return "FATURA_ISLENIYOR";
            if (status is InvoiceStatus.Cancelled or InvoiceStatus.CancelledLocal) return "FATURA_IPTAL";
            if (status == InvoiceStatus.Completed)
                return "FATURA_KONTROLDE";
            if (status is InvoiceStatus.Submitting or InvoiceStatus.Submitted)
                return "FATURA_ISLENIYOR";
            if (!string.IsNullOrWhiteSpace(invoiceNumber) && status is not (InvoiceStatus.Draft or InvoiceStatus.Validating or InvoiceStatus.Ready))
                return "FATURA_KONTROLDE";
            return "FATURA_ISLENIYOR";
        }
        // Trendyol's order payloads expose the same business fact in two
        // different places depending on the endpoint/version: invoiceStatus
        // in the package snapshot, or the package's raw status as INVOICED.
        // RawStatus must be considered before the snapshot fallback because a
        // later shipment update can replace the snapshot's invoice fields.
        if (packageRawStatuses.Any(IsInvoicedRemoteStatus))
            return "FATURA_KONTROLDE";

        var remote = JsonText(customerJson, "marketplaceInvoiceStatus", "invoiceStatus")?.Trim().ToUpperInvariant();
        if (remote is "INVOICED") return "FATURA_KONTROLDE";
        if (remote is "RECEIVED") return "FATURA_KONTROLDE";
        if (remote is "REJECTED") return "FATURA_REDDEDILDI";
        if (remote is "NOTINVOICED" or "NOT_INVOICED") return "FATURA_BEKLIYOR";
        // Missing marketplace evidence is not the same as an explicit
        // NotInvoiced response. Historical Trendyol payloads often omit the
        // invoice field entirely; presenting those packages as actionable
        // invoice work creates false Dashboard counts.
        return "FATURA_BILINMIYOR";
    }

    internal static string InvoiceLabel(Invoice? invoice, MarketplaceInvoiceStatus marketplaceStatus, string customerJson, IEnumerable<string?> packageRawStatuses, bool marketplaceDeliveryConfirmed = false) =>
        InvoiceLabel(invoice?.Status, invoice?.InvoiceNumber, marketplaceStatus, customerJson, packageRawStatuses, marketplaceDeliveryConfirmed);

    internal static string InvoiceLabel(InvoiceStatus? invoiceStatus, string? invoiceNumber, MarketplaceInvoiceStatus marketplaceStatus, string customerJson, IEnumerable<string?> packageRawStatuses, bool marketplaceDeliveryConfirmed = false)
    {
        if (invoiceStatus is { } status)
        {
            if (status == InvoiceStatus.UnknownResult) return "FATURA_BELIRSIZ";
            if (status is InvoiceStatus.Rejected or InvoiceStatus.ValidationFailed or InvoiceStatus.ManualReview or InvoiceStatus.MarketplaceFailed)
                return status == InvoiceStatus.MarketplaceFailed ? "FATURA_PLATFORMA_AKTARILMADI" : "FATURA_REDDEDILDI";
            if (status is InvoiceStatus.Cancelled or InvoiceStatus.CancelledLocal)
                return "FATURA_IPTAL";
            if (status is InvoiceStatus.Accepted or InvoiceStatus.MarketplacePending or InvoiceStatus.Submitted or InvoiceStatus.Submitting)
                return "FATURA_ISLENIYOR";
            // The marketplace observation is authoritative for the delivery
            // leg. A local invoice number only proves that our fiscal provider
            // created a document; it does not prove Trendyol accepted it.
            if (status == InvoiceStatus.Completed) return "FATURA_KONTROLDE";
            if (marketplaceStatus == MarketplaceInvoiceStatus.Rejected) return "FATURA_REDDEDILDI";
            if (marketplaceStatus == MarketplaceInvoiceStatus.Received) return "FATURA_KONTROLDE";
            return InvoiceLabel(invoiceStatus, invoiceNumber, customerJson, packageRawStatuses, marketplaceDeliveryConfirmed);
        }
        return marketplaceStatus switch
        {
            MarketplaceInvoiceStatus.Invoiced => "FATURA_KONTROLDE",
            MarketplaceInvoiceStatus.Received => "FATURA_KONTROLDE",
            MarketplaceInvoiceStatus.Rejected => "FATURA_REDDEDILDI",
            MarketplaceInvoiceStatus.NotInvoiced => "FATURA_BEKLIYOR",
            _ => InvoiceLabel(invoiceStatus, invoiceNumber, customerJson, packageRawStatuses, marketplaceDeliveryConfirmed)
        };
    }

    internal static string ReturnInvoiceLabel(Invoice? invoice, MarketplaceInvoiceStatus marketplaceStatus, string customerJson, IEnumerable<string?> packageRawStatuses, bool marketplaceDeliveryConfirmed = false)
    {
        // An absent marketplace observation does not prove that the package is
        // waiting for an invoice. Keep unknown separate so the return list does
        // not report a false pending state when Trendyol has not exposed it.
        return InvoiceLabel(invoice, marketplaceStatus, customerJson, packageRawStatuses, marketplaceDeliveryConfirmed);
    }

    // A status the seller set by hand on the invoice page ("Faturası yüklendi" / "Fatura bekliyor", for marketplaces
    // whose API cannot show an invoice uploaded in their own seller panel) decides before any other evidence.
    internal static string? ManualInvoiceLabel(string? manualInvoiceStatus) =>
        manualInvoiceStatus?.Trim().ToUpperInvariant() switch
        {
            "UPLOADED" => "FATURA_KONTROLDE",
            "PENDING" => "FATURA_BEKLIYOR",
            _ => null
        };

    // The hand-set status itself ("UPLOADED" / "PENDING"), so the screens can say the label was set by hand.
    internal static string? HandSetInvoiceStatus(string? manualInvoiceStatus) =>
        ManualInvoiceLabel(manualInvoiceStatus) is null ? null : manualInvoiceStatus!.Trim().ToUpperInvariant();

    // The orders filter and the row label read an issued invoice with these same two rules (P4, 1 Nov 2026):
    // - not on the platform: the marketplace leg failed, or it stopped with an error while waiting;
    // - needs review: the provider issued it (it has an external reference) and it stopped for review, for example
    //   because the marketplace already shows an invoice. That is not a refusal ("Reddedildi").
    internal static readonly Expression<Func<Invoice, bool>> IssuedInvoiceNotOnPlatform = invoice =>
        invoice.Status == InvoiceStatus.MarketplaceFailed
        || ((invoice.Status == InvoiceStatus.Accepted || invoice.Status == InvoiceStatus.MarketplacePending) && invoice.LastErrorCode != null);

    internal static readonly Expression<Func<Invoice, bool>> IssuedInvoiceNeedsReview = invoice =>
        invoice.Status == InvoiceStatus.ManualReview && invoice.ExternalReference != null && invoice.ExternalReference != "";

    private static readonly Func<Invoice, bool> IsIssuedInvoiceNotOnPlatform = IssuedInvoiceNotOnPlatform.Compile();
    private static readonly Func<Invoice, bool> IsIssuedInvoiceNeedsReview = IssuedInvoiceNeedsReview.Compile();

    internal const string InvoiceNeedsReviewLabel = "FATURA_INCELEME_GEREKLI";

    internal static string? IssuedInvoiceLabel(Invoice invoice) =>
        IsIssuedInvoiceNeedsReview(invoice) ? InvoiceNeedsReviewLabel
        : IsIssuedInvoiceNotOnPlatform(invoice) ? "FATURA_PLATFORMA_AKTARILMADI"
        : null;

    // The one invoice label of a package: the orders list, the order detail and the invoice page read it the same way.
    internal static string PackageInvoiceLabel(ShipmentPackage? package, Invoice? invoice, string customerJson, IEnumerable<string?> packageRawStatuses, string? platformCode, bool marketplaceDeliveryConfirmed = false) =>
        ManualInvoiceLabel(package?.ManualInvoiceStatus)
            ?? InvoiceLabelForPlatform(invoice, package?.MarketplaceInvoiceStatus ?? MarketplaceInvoiceStatus.Unknown, customerJson, packageRawStatuses, platformCode, marketplaceDeliveryConfirmed);

    // The fiscal states the invoice workspace labels (pre-provider refusal, lost answer, adjustment) read the same here.
    internal static string InvoiceLabelForPlatform(Invoice? invoice, MarketplaceInvoiceStatus marketplaceStatus, string customerJson, IEnumerable<string?> packageRawStatuses, string? platformCode, bool marketplaceDeliveryConfirmed = false)
        => invoice is not null && InvoicingBillingService.SharedFiscalLabel(invoice.Status, invoice.LastErrorCode, invoice.ExternalReference) is { } shared
            ? shared
            : invoice is not null && IssuedInvoiceLabel(invoice) is { } issued
            ? issued
            : InvoiceLabelForPlatform(invoice?.Status, invoice?.InvoiceNumber, marketplaceStatus, customerJson, packageRawStatuses, platformCode, marketplaceDeliveryConfirmed);

    internal static string InvoiceLabelForPlatform(InvoiceStatus? invoiceStatus, string? invoiceNumber, MarketplaceInvoiceStatus marketplaceStatus, string customerJson, IEnumerable<string?> packageRawStatuses, string? platformCode, bool marketplaceDeliveryConfirmed = false)
    {
        var label = InvoiceLabel(invoiceStatus, invoiceNumber, marketplaceStatus, customerJson, packageRawStatuses, marketplaceDeliveryConfirmed);
        if (string.Equals(platformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase))
        {
            if (invoiceStatus == InvoiceStatus.Draft) return "FATURA_BEKLIYOR";
            if (invoiceStatus == InvoiceStatus.Completed) return "FATURA_KONTROLDE";
            // Shopify holds only the link we write (ravencia.invoice_pdf_url): without a panel invoice and without
            // that link nothing says whether the order was invoiced elsewhere, so it is to be reviewed, not waiting.
            if (invoiceStatus is null && label is "FATURA_BILINMIYOR" or "FATURA_BEKLIYOR") return ShopifyUnconfirmedInvoiceLabel;
            if (label == "FATURA_BILINMIYOR") return "FATURA_BEKLIYOR";
        }
        // n11 gives no invoice state, only the link of an invoice that was attached to the order (empty until
        // one is); Pazarama gives nothing at all. A link means an invoice exists at the marketplace but no local
        // record matches it, so it is shown for review.
        else if (label == "FATURA_BILINMIYOR" && string.Equals(platformCode, "N11", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(JsonText(customerJson, "invoiceLink")))
            return "FATURA_KONTROLDE";
        // n11, PTT AVM and Pazarama never show an invoice uploaded in their seller panel, so an unknown state there
        // cannot be checked from the marketplace: the panel's own record decides (owner, 24 Oct 2026 for n11 / PTT
        // AVM, 31 Oct 2026 for Pazarama). No panel invoice is waiting for an invoice; an invoice the panel issued and
        // delivered marks the package invoiced (RecordConfirmedDelivery) and reads as issued.
        else if (label == "FATURA_BILINMIYOR" && IsUnverifiableInvoicePlatform(platformCode))
            return UnverifiedInvoiceLabel;
        return label;
    }

    internal const string UnverifiedInvoiceLabel = "FATURA_BEKLIYOR";

    // "İncelenecek": a Shopify order with no panel invoice and no invoice link of ours. It is still invoice work: it
    // counts and is due exactly as "Fatura Bekliyor" does (IsAwaitingInvoiceLabel).
    internal const string ShopifyUnconfirmedInvoiceLabel = "FATURA_INCELENECEK";

    internal static bool IsAwaitingInvoiceLabel(string? label) => label is "FATURA_BEKLIYOR" or ShopifyUnconfirmedInvoiceLabel;

    internal static readonly string[] UnverifiableInvoicePlatforms = ["N11", "PTTAVM", "PAZARAMA"];

    internal static bool IsUnverifiableInvoicePlatform(string? platformCode) =>
        platformCode?.Trim().ToUpperInvariant() is "N11" or "PTTAVM" or "PAZARAMA";

    internal static ShipmentPackage? SelectDisplayPackage(IEnumerable<ShipmentPackage> packages, string? platformCode)
    {
        var ordered = packages.OrderByDescending(x => x.StatusOccurredAt).ThenByDescending(x => x.UpdatedAt).ToList();
        // The package an order card shows is a live one; a cancelled split package only when
        // nothing else is left.
        if (!string.Equals(platformCode, "SHOPIFY", StringComparison.OrdinalIgnoreCase))
            return ordered.FirstOrDefault(x => x.Status != ShipmentPackageStatus.Cancelled) ?? ordered.FirstOrDefault();

        var visible = ordered.Where(x => !(x.ExternalPackageId.StartsWith("order:", StringComparison.OrdinalIgnoreCase)
            && x.ExternalPackageId.EndsWith(":remainder", StringComparison.OrdinalIgnoreCase))).ToList();
        return visible.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.CargoTrackingNumber))
            ?? visible.FirstOrDefault(x => x.Status != ShipmentPackageStatus.Cancelled)
            ?? visible.FirstOrDefault();
    }

    private static bool IsInvoicedRemoteStatus(string? status) =>
        string.Equals(status?.Trim(), "INVOICED", StringComparison.OrdinalIgnoreCase);

    private static string? InvoiceDocumentUrl(string customerJson)
    {
        return ValidInvoiceDocumentUrl(JsonText(customerJson, "invoiceLink"));
    }

    private static string? ValidInvoiceDocumentUrl(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.ToString() : null;
    }

    // n11 facts the order card shows besides the shared fields: cargo tracking (number and carrier
    // link), delivery type, commission rate and the discounts split by who paid them.
    internal static IReadOnlyList<OrderInfoItem> N11OrderInfo(IEnumerable<string?> lineSnapshots, string customerSnapshotJson)
    {
        var info = new List<OrderInfoItem>();
        var trackingNumber = JsonText(customerSnapshotJson, "cargoSenderNumber");
        var trackingLink = JsonText(customerSnapshotJson, "cargoTrackingLink");
        var safeLink = Uri.TryCreate(trackingLink, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.ToString() : null;
        if (trackingNumber is not null) info.Add(new("Kargo takip no", trackingNumber, safeLink));
        if (JsonText(customerSnapshotJson, "deliveryAddressType") is { } deliveryType) info.Add(new("Teslimat tipi", deliveryType));

        decimal mallDiscount = 0, sellerDiscount = 0;
        var rates = new SortedSet<decimal>();
        foreach (var snapshot in lineSnapshots)
        {
            if (string.IsNullOrWhiteSpace(snapshot)) continue;
            try
            {
                using var document = JsonDocument.Parse(snapshot);
                var line = document.RootElement;
                if (line.ValueKind != JsonValueKind.Object) continue;
                mallDiscount += JsonAmount(line, "totalMallDiscountPrice");
                sellerDiscount += JsonAmount(line, "totalSellerDiscountPrice");
                if (line.TryGetProperty("commissionRate", out var rate) && rate.ValueKind == JsonValueKind.Number && rate.TryGetDecimal(out var commission))
                    rates.Add(commission - JsonAmount(line, "sellerCampaignCommissionRate"));
            }
            catch (JsonException) { }
        }
        var tr = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
        if (rates.Count > 0) info.Add(new("Komisyon oranı", string.Join(" / ", rates.Select(rate => "%" + rate.ToString("0.##", tr)))));
        if (mallDiscount > 0) info.Add(new("N11 indirimi", mallDiscount.ToString("N2", tr) + " ₺"));
        if (sellerDiscount > 0) info.Add(new("Satıcı indirimi", sellerDiscount.ToString("N2", tr) + " ₺"));
        return info;
    }

    private static decimal JsonAmount(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var amount) ? amount : 0;

    private static SourceLineView SourceLine(string? json)
    {
        var snapshot = json ?? "{}";
        var image = NormalizeImageUrl(SourceImageUrl(snapshot));
        var color = JsonText(snapshot, "productColor", "color", "colorName");
        var size = JsonText(snapshot, "productSize", "size", "sizeName");
        var options = new List<string>();
        if (!string.IsNullOrWhiteSpace(color)) options.Add($"Renk: {color}");
        if (!string.IsNullOrWhiteSpace(size)) options.Add($"Beden: {size}");
        return new(image, JsonText(snapshot, "modelCode"), options.Count == 0 ? null : string.Join(" | ", options));
    }

    // The product's own image fields, the most specific first. The first field that holds a usable address wins,
    // wherever it sits in the snapshot: a match in document order picked a claim photo listed before the product's.
    private static readonly string[] ProductImageFields = ["productImageUrl", "productImageUrlFormat", "emaproductImageUrlFormat", "imageUrl", "productImage", "image"];

    // Parts of a line snapshot whose images are not the product: a Hepsiburada claim merged into the line keeps the
    // customer's evidence photos under Reports[].ImageUrl (an images.hepsiburada.net/oms/claim address the panel cannot load).
    private static readonly HashSet<string> NonProductImageContainers = new(StringComparer.OrdinalIgnoreCase) { "Reports" };

    internal static string? SourceImageUrl(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return FindImageUrl(document.RootElement)
                ?? ProductImageFields.Select(field => FindImageField(document.RootElement, field)).FirstOrDefault(value => value is not null);
        }
        catch (JsonException) { return null; }
    }

    // The line's image and, when it is the marketplace's, the catalog's photo to show if that address no longer loads.
    internal static (string? ImageUrl, string? FallbackImageUrl) LineImageUrls(string? sourceImageUrl, string? catalogImageUrl) =>
        sourceImageUrl is null
            ? (catalogImageUrl, null)
            : (sourceImageUrl, catalogImageUrl is null || string.Equals(catalogImageUrl, sourceImageUrl, StringComparison.Ordinal) ? null : catalogImageUrl);

    // A return detail line's photo: the marketplace's with the catalog's as fallback (LineImageUrls); with neither, the
    // barcode lookup (/orders/product-image) is the photo.
    // The lookup carries the claim's store (connectionId) when known, as the order screens' own lookup does.
    internal static (string? ImageUrl, string? FallbackImageUrl) ReturnDetailLineImageUrls(string? sourceImageUrl, string? catalogImageUrl, string? barcode, Guid? connectionId = null)
    {
        var images = LineImageUrls(sourceImageUrl, catalogImageUrl);
        return images.ImageUrl is null && !string.IsNullOrWhiteSpace(barcode)
            ? ($"/api/v1/orders/product-image?barcode={Uri.EscapeDataString(barcode)}{(connectionId is { } id && id != Guid.Empty ? $"&connectionId={id:D}" : "")}", null)
            : images;
    }

    private static string? FindImageField(JsonElement element, string field)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (NonProductImageContainers.Contains(property.Name)) continue;
                if (property.Name.Equals(field, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                {
                    var value = property.Value.GetString();
                    if (NormalizeImageUrl(value) is not null) return value;
                }
                var nested = FindImageField(property.Value, field); if (nested is not null) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) { var nested = FindImageField(item, field); if (nested is not null) return nested; }
        return null;
    }

    internal static string? NormalizeImageUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var candidate = value.Trim();
        // Hepsiburada returns productImageUrlFormat with a {size} token.
        // Keep the stored marketplace snapshot untouched and resolve the token
        // for the order UI when projecting the image URL.
        candidate = candidate.Replace("{size}", "500", StringComparison.OrdinalIgnoreCase);
        if (candidate.StartsWith("//", StringComparison.Ordinal)) candidate = "https:" + candidate;
        else if (candidate.StartsWith("/", StringComparison.Ordinal)) candidate = "https://cdn.dsmcdn.com" + candidate;
        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.ToString() : null;
    }

    private static string? FindImageUrl(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (NonProductImageContainers.Contains(property.Name)) continue;
                if (property.Name.Equals("images", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.Array)
                    foreach (var image in property.Value.EnumerateArray())
                    {
                        var url = image.ValueKind == JsonValueKind.Object && image.TryGetProperty("url", out var value) ? value.ToString() : image.ToString();
                        if (NormalizeImageUrl(url) is not null) return url;
                    }
                var nested = FindImageUrl(property.Value); if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) { var nested = FindImageUrl(item); if (!string.IsNullOrWhiteSpace(nested)) return nested; }
        return null;
    }

    private sealed record SourceLineView(string? ImageUrl, string? ModelCode, string? OptionSignature);

    internal static string? MaskIdentityNumber(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;
        return trimmed.Length > 4 ? $"***{trimmed[^4..]}" : "***";
    }

    // Only the delivery fields a label prints: no phone, e-mail, identity or tax number.
    internal static OrderAddressView? ListAddress(string shipmentAddressJson)
    {
        var name = NameFrom(shipmentAddressJson, "firstName", "lastName", "shippingFirstName", "shippingLastName", "fullName", "name", "company", "companyName");
        var address = new OrderAddressView(
            IsMeaningfulText(name) ? name : null,
            JsonText(shipmentAddressJson, "fullAddress"),
            JsonText(shipmentAddressJson, "address1"),
            JsonText(shipmentAddressJson, "address2"),
            JsonText(shipmentAddressJson, "neighborhood"),
            JsonText(shipmentAddressJson, "district", "countyName"),
            JsonText(shipmentAddressJson, "city", "province"),
            JsonText(shipmentAddressJson, "postalCode", "zip"),
            JsonText(shipmentAddressJson, "countryCode", "country"),
            JsonBool(shipmentAddressJson, "isDeliveryPoint"),
            JsonText(shipmentAddressJson, "deliveryPointType"));
        return address == new OrderAddressView(null, null, null, null, null, null, null, null, null) ? null : address;
    }

    private static string? JsonText(string json, params string[] names)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return FindText(document.RootElement, new HashSet<string>(names, StringComparer.OrdinalIgnoreCase));
        }
        catch (JsonException) { return null; }
    }

    private static string? FindText(JsonElement element, HashSet<string> names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (names.Contains(property.Name) && property.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.Object or JsonValueKind.Array))
                {
                    var value = property.Value.ToString();
                    if (IsMeaningfulText(value)) return value;
                }
                var nested = FindText(property.Value, names); if (!string.IsNullOrWhiteSpace(nested)) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) { var nested = FindText(item, names); if (!string.IsNullOrWhiteSpace(nested)) return nested; }
        return null;
    }

    private static bool IsMeaningfulText(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Any(char.IsLetterOrDigit);

    private static bool JsonBool(string json, params string[] names)
    {
        var value = JsonText(json, names);
        return bool.TryParse(value, out var parsed) && parsed || value is "1" or "YES" or "EVET";
    }

    private static bool? JsonNullableBool(string json, params string[] names)
    {
        var value = JsonText(json, names);
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (bool.TryParse(value, out var parsed)) return parsed;
        return value is "1" or "YES" or "EVET" ? true : value is "0" or "NO" or "HAYIR" ? false : null;
    }

    private static DateTimeOffset? JsonInstant(string json, params string[] names)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var namesSet = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            return FindInstant(document.RootElement, namesSet);
        }
        catch (JsonException) { return null; }
    }

    private static DateTimeOffset? FindInstant(JsonElement element, HashSet<string> names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (names.Contains(property.Name))
                {
                    if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out var num) && num > 0)
                    {
                        try { return DateTimeOffset.FromUnixTimeMilliseconds(num); } catch (ArgumentOutOfRangeException) { }
                    }
                    else if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        var str = property.Value.GetString();
                        if (long.TryParse(str, out var numStr) && numStr > 0)
                        {
                            try { return DateTimeOffset.FromUnixTimeMilliseconds(numStr); } catch (ArgumentOutOfRangeException) { }
                        }
                        if (DateTimeOffset.TryParse(str, out var parsed)) return parsed;
                    }
                }
                var nested = FindInstant(property.Value, names);
                if (nested is not null) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindInstant(item, names);
                if (nested is not null) return nested;
            }
        }
        return null;
    }
    private static ShipmentView Map(ShipmentPackage x, string orderNumber) => new(x.Id, x.OrderId, orderNumber, x.ExternalPackageId, Wire(x.Status), x.RawStatus, x.CargoTrackingNumber, x.StatusOccurredAt, x.Version, x.CargoProviderExternalId, ShipmentPackageClassification.IsResend(x.CreatedBy, x.OriginExternalPackageId), x.UnreadableLineCount, HandSetInvoiceStatus(x.ManualInvoiceStatus), x.CargoBarcode);
    private static string Wire<T>(T value) where T : Enum => string.Concat(value.ToString().Select((ch, index) => char.IsUpper(ch) && index > 0 ? "_" + ch : ch.ToString())).ToUpperInvariant();
    private const string VerifyingCode = "EXTERNAL_EFFECT_VERIFYING";
    private const string VerifyingMessage = "Pazaryerinin yanıtı kesinleşmedi; sonuç birkaç saniye içinde pazaryerinden okunup panel güncellenecek. İşlemi yeniden göndermeyin.";

    private static ServiceResult<T> Verifying<T>() => ServiceResult<T>.Fail(VerifyingCode, VerifyingMessage, 409);

    // The marketplace's answer was unclear: the action is not sent again; a verification job
    // reads the marketplace and settles the effect (see MarketplaceJobProcessor.ReconcileExternalEffect).
    private async Task<ServiceResult<T>> VerifyLaterAsync<T>(ExternalEffectRecord effect, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var dedup = ExternalEffectReconcilePolicy.JobDedupKey(effect.Id);
        if (!await db.IntegrationJobs.AnyAsync(x => x.TenantId == effect.TenantId && x.JobDedupKey == dedup, cancellationToken))
            db.IntegrationJobs.Add(ExternalEffectReconcilePolicy.NewJob(effect, now, now + ExternalEffectReconcilePolicy.FirstCheckDelay));
        await db.SaveChangesAsync(cancellationToken);
        return Verifying<T>();
    }

    private static async Task<ServiceResult<T>> QueueInsteadAsync<T>(Task<ServiceResult<Guid>> enqueue)
    {
        var queued = await enqueue;
        return queued.Error is { } error
            ? ServiceResult<T>.Fail(error.Code, error.Message, error.Status, error.FieldErrors)
            : ServiceResult<T>.Fail("ACTION_QUEUED", "Pazaryeri istek kotası şu an dolu; işlem sıraya alındı ve birkaç saniye içinde gönderilecek.", 409);
    }

    private static bool IsAmbiguous(AdapterError error) => WriteOutcomePolicy.IsUncertain(error);
    private static ServiceResult<T> Invalid<T>(string field, string message) => ServiceResult<T>.Fail("VALIDATION_FAILED", message, 422, new Dictionary<string, string[]> { [field] = [message] });
    private static ServiceResult<T> NotFound<T>() => ServiceResult<T>.Fail("RESOURCE_NOT_FOUND", "Kayıt bulunamadı.", 404);
    private static ServiceResult<T> Precondition<T>(long version) => ServiceResult<T>.Fail("CONCURRENCY_CONFLICT", "Kayıt siz düzenlerken başka bir işlemle güncellendi. Sayfayı yenileyip tekrar deneyin.", 412);
}
