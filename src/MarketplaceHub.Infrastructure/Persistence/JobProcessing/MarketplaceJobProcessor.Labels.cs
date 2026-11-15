using System.Security.Cryptography;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed partial class MarketplaceJobProcessor
{
    private async Task<JobExecutionResult> LabelCapabilityProbe(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        CapabilityProbeJobPayload? payload;
        try { payload = JsonSerializer.Deserialize<CapabilityProbeJobPayload>(payloadJson, JsonOptions); }
        catch (JsonException) { return JobExecutionResult.Blocked("CAPABILITY_PROBE_PAYLOAD_INVALID", "Stage capability canary payloadı geçersiz."); }
        if (payload is null || payload.JobId == Guid.Empty || payload.PackageId == Guid.Empty || payload.ActorUserId == Guid.Empty || payload.CapabilityCode is not (MarketplaceCapabilities.LabelRead or MarketplaceCapabilities.LabelWrite)) return JobExecutionResult.Blocked("CAPABILITY_PROBE_PAYLOAD_INVALID", "Stage capability canary zorunlu alanları geçersiz.");
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && x.PlatformCode == "TRENDYOL", cancellationToken);
        if (connection is null || !string.Equals(connection.Environment, "STAGE", StringComparison.OrdinalIgnoreCase) || !string.Equals(connection.ExternalStoreId, "2738", StringComparison.Ordinal)) return JobExecutionResult.Blocked("STAGE_CONNECTION_REQUIRED", "Capability canary yalnız Trendyol STAGE seller 2738 bağlantısında çalışır.");
        var package = await db.ShipmentPackages.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == payload.PackageId && x.ConnectionId == connectionId, cancellationToken);
        if (package is null || string.IsNullOrWhiteSpace(package.CargoTrackingNumber)) return JobExecutionResult.Blocked("CAPABILITY_PROBE_TARGET_INVALID", "Canary için takip numaralı Stage paketi bulunamadı.");
        var context = Context(tenantId, connectionId, correlationId, $"stage-capability-probe:{payload.JobId:N}") with { IsStageCapabilityProbe = true };
        if (payload.CapabilityCode == MarketplaceCapabilities.LabelWrite)
        {
            if (!CommonLabelCarrierPolicy.Supports(package.CargoProviderExternalId)) return JobExecutionResult.Blocked("COMMON_LABEL_CARRIER_UNSUPPORTED", "LABEL_WRITE canary yalnız Trendyol öder Aras Kargo veya TEX Stage paketi üzerinde çalışır.");
            var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == package.OrderId, cancellationToken);
            var latestFixture = await db.AuditLogs.AsNoTracking().Where(x => x.TenantId == tenantId && x.Action == "STAGE_TEST_ORDER_CREATED" && x.TargetType == "StageTestOrder").OrderByDescending(x => x.CreatedAt).Select(x => x.TargetId).FirstOrDefaultAsync(cancellationToken);
            if (order is null || !string.Equals(order.OrderNumber, latestFixture, StringComparison.Ordinal)) return JobExecutionResult.Blocked("STAGE_LABEL_FRESH_FIXTURE_REQUIRED", "LABEL_WRITE canary yalnız en son oluşturulan auditli Stage Test Order paketi üzerinde çalışır.");
            var lines = await db.OrderLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.OrderId == package.OrderId).Select(x => new { x.ExternalLineId, x.OrderedQuantity }).ToListAsync(cancellationToken);
            if (lines.Count != 1 || !long.TryParse(lines[0].ExternalLineId, out var lineId) || lines[0].OrderedQuantity <= 0 || lines[0].OrderedQuantity != decimal.Truncate(lines[0].OrderedQuantity) || lines[0].OrderedQuantity > int.MaxValue) return JobExecutionResult.Blocked("STAGE_LABEL_PICKING_PAYLOAD_INVALID", "Taze Stage fixture için tek ve geçerli satır kimliği/miktarı gerekir.");
            TrackRequest();
            var picking = await orders.ExecutePackageActionAsync(context, new PackageActionCommand(package.ExternalPackageId, "PICKING", JsonSerializer.Serialize(new { lines = new[] { new { lineId, quantity = (int)lines[0].OrderedQuantity } }, @params = new { }, status = "Picking" })), cancellationToken);
            if (!picking.IsSuccess) { TrackResultFailure(picking.Error); throw JobProcessingException.FromAdapter(picking.Error!); }
            TrackRequest();
            var created = await orders.CreateCommonLabelAsync(context, new CommonLabelRequest(package.CargoTrackingNumber, payload.BoxQuantity, payload.VolumetricHeight), cancellationToken);
            if (!created.IsSuccess) { TrackResultFailure(created.Error); throw JobProcessingException.FromAdapter(created.Error!); }
        }
        TrackRequest();
        var document = await orders.GetCommonLabelAsync(context, package.CargoTrackingNumber, cancellationToken);
        if (!document.IsSuccess) { TrackResultFailure(document.Error); throw JobProcessingException.FromAdapter(document.Error!); }
        TrackReceived();
        var hash = Convert.ToHexString(SHA256.HashData(document.Value!.Content));
        var codes = payload.CapabilityCode == MarketplaceCapabilities.LabelWrite ? new[] { MarketplaceCapabilities.LabelRead, MarketplaceCapabilities.LabelWrite, MarketplaceCapabilities.ShipmentWrite } : new[] { MarketplaceCapabilities.LabelRead };
        var now = timeProvider.GetUtcNow();
        foreach (var code in codes)
        {
            var capability = await db.PlatformCapabilities.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Code == code, cancellationToken);
            if (capability is null) continue;
            capability.SupportLevel = CapabilitySupportLevel.Supported;
            capability.SourceUrl = code == MarketplaceCapabilities.LabelRead ? "https://developers.trendyol.com/v2.0/docs/common-label-barcode-get-integration" : "https://developers.trendyol.com/v2.0/docs/common-label-barcode-request-createcommonlabel";
            capability.SourceVersion = "V2";
            capability.Environment = connection.Environment;
            capability.StoreScope = connection.ExternalStoreId;
            capability.ConstraintsJson = code == MarketplaceCapabilities.ShipmentWrite ? JsonSerializer.Serialize(new { allowedActions = new[] { "PICKING" } }) : JsonSerializer.Serialize(new { formats = new[] { document.Value.Format } });
            capability.EvidenceNote = code == MarketplaceCapabilities.ShipmentWrite
                ? "SHIPMENT_WRITE Stage canary, en son auditli test fixture üzerinde resmî PICKING isteğini ve ardından ortak etiket create/read-back zincirini başarıyla doğruladı."
                : $"{code} Stage canary gerçek paket/etiket read-back ile başarılı; private fixture SHA-256 kaydedildi.";
            capability.FixtureChecksum = hash;
            capability.VerifiedAt = now;
            capability.Version++;
            db.AuditLogs.Add(new AuditLog { TenantId = tenantId, ActorUserId = payload.ActorUserId, Action = "CAPABILITY_STAGE_PROBE_SUCCEEDED", TargetType = "PlatformCapability", TargetId = capability.Id.ToString("D"), Reason = $"{code}:package:{package.Id:D}", CorrelationId = correlationId, CreatedAt = now });
        }
        await db.SaveChangesAsync(cancellationToken);
        return JobExecutionResult.Success();
    }

    private async Task<JobExecutionResult> CreateStageTestOrder(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        StageTestOrderJobPayload? payload;
        try { payload = JsonSerializer.Deserialize<StageTestOrderJobPayload>(payloadJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (JsonException) { return JobExecutionResult.Blocked("STAGE_TEST_ORDER_PAYLOAD_INVALID", "Stage test siparişi payloadı geçersiz."); }
        if (payload is null || payload.JobId == Guid.Empty || payload.ActorUserId == Guid.Empty || string.IsNullOrWhiteSpace(payload.Barcode)) return JobExecutionResult.Blocked("STAGE_TEST_ORDER_PAYLOAD_INVALID", "Stage test siparişi zorunlu alanları eksik.");
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && (x.PlatformCode == "TRENDYOL" || x.PlatformCode == "HEPSIBURADA"), cancellationToken);
        if (connection is null || !string.Equals(connection.Environment, "STAGE", StringComparison.OrdinalIgnoreCase)
            || connection.PlatformCode == "TRENDYOL" && !string.Equals(connection.ExternalStoreId, "2738", StringComparison.Ordinal))
            return JobExecutionResult.Blocked("STAGE_TEST_ORDER_SCOPE_REQUIRED", "Stage test siparişi yalnız desteklenen Stage bağlantılarında kullanılabilir.");
        var sku = payload.Barcode;
        var importedHepsiburadaSku = payload.HepsiburadaSku?.Trim();
        if (connection.PlatformCode == "HEPSIBURADA" && !string.IsNullOrEmpty(importedHepsiburadaSku))
        {
            // SIT fallback chosen at enqueue (STAGE only, checked above): the hbSku of an imported product
            // with no LIVE listing. It must still be a linked hbSku with an ACTIVE offer on this connection.
            var importedExternalIds = await (from link in db.MarketplaceVariantLinks.AsNoTracking()
                                             join offer in db.ChannelOffers.AsNoTracking() on new { link.TenantId, link.ConnectionId, link.VariantId } equals new { offer.TenantId, offer.ConnectionId, offer.VariantId }
                                             where link.TenantId == tenantId && link.ConnectionId == connectionId && offer.Status == "ACTIVE"
                                             select link.ExternalId).ToListAsync(cancellationToken);
            if (!HepsiburadaStageCertificationPolicy.LooksLikeHepsiburadaSku(importedHepsiburadaSku)
                || !importedExternalIds.Any(externalId => string.Equals(externalId.Trim(), importedHepsiburadaSku, StringComparison.OrdinalIgnoreCase)))
                return JobExecutionResult.Blocked("HEPSIBURADA_STAGE_SKU_NOT_LINKED", "Test siparişi için seçilen hbSku artık bu Stage bağlantısında ACTIVE teklife bağlı değil.");
            sku = importedHepsiburadaSku;
        }
        else if (connection.PlatformCode == "HEPSIBURADA")
        {
            var remoteSkus = await (from listing in db.ChannelListingVariants.AsNoTracking()
                                    join profile in db.ChannelListingProfiles.AsNoTracking() on new { listing.TenantId, ProfileId = listing.ProfileId } equals new { profile.TenantId, ProfileId = profile.Id }
                                    join state in db.MarketplaceListingStates.AsNoTracking() on new { listing.TenantId, profile.ConnectionId, listing.VariantId } equals new { state.TenantId, state.ConnectionId, state.VariantId }
                                    join link in db.MarketplaceVariantLinks.AsNoTracking() on new { listing.TenantId, profile.ConnectionId, listing.VariantId } equals new { link.TenantId, link.ConnectionId, link.VariantId }
                                    where listing.TenantId == tenantId && profile.ConnectionId == connectionId && profile.Enabled && state.ActualStatus == "LIVE"
                                          && listing.ExternalBarcode == payload.Barcode && !string.IsNullOrWhiteSpace(link.ExternalId)
                                    select link.ExternalId).Distinct().ToListAsync(cancellationToken);
            if (remoteSkus.Count != 1) return JobExecutionResult.Blocked("HEPSIBURADA_STAGE_LISTING_AMBIGUOUS", "Test sipariş barkodu için tek bir Hepsiburada hbSku eşleşmesi bulunamadı.");
            sku = remoteSkus[0];
        }
        TrackRequest();
        var result = await orders.CreateStageTestOrderAsync(Context(tenantId, connectionId, correlationId, $"stage-test-order:{payload.JobId:N}") with { IsStageCapabilityProbe = true }, sku, cancellationToken);
        if (!result.IsSuccess) { TrackResultFailure(result.Error); throw JobProcessingException.FromAdapter(result.Error!); }
        TrackReceived();
        db.AuditLogs.Add(new AuditLog { TenantId = tenantId, ActorUserId = payload.ActorUserId, Action = "STAGE_TEST_ORDER_CREATED", TargetType = "StageTestOrder", TargetId = result.Value!.OrderNumber, Reason = $"fresh-label-write-fixture:{connection.PlatformCode}", CorrelationId = correlationId, CreatedAt = timeProvider.GetUtcNow() });
        await db.SaveChangesAsync(cancellationToken);
        return JobExecutionResult.Success();
    }

    private async Task<JobExecutionResult> CommonLabel(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        CommonLabelJobPayload? payload;
        try { payload = JsonSerializer.Deserialize<CommonLabelJobPayload>(payloadJson, JsonOptions); }
        catch (JsonException) { return JobExecutionResult.Blocked("COMMON_LABEL_PAYLOAD_INVALID", "Ortak etiket işi payload sözleşmesini sağlamıyor."); }
        if (payload is null || payload.JobId == Guid.Empty || payload.PackageId == Guid.Empty || payload.BoxQuantity is < 1 or > 50 || payload.VolumetricHeight is < 0 or > 10000 || payload.DeadlineAt <= payload.StartedAt || string.IsNullOrWhiteSpace(payload.Phase)) return JobExecutionResult.Blocked("COMMON_LABEL_PAYLOAD_INVALID", "Ortak etiket işi zorunlu alanları eksik veya sınır dışında.");
        var job = await db.IntegrationJobs.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == payload.JobId && x.ConnectionId == connectionId && x.JobType == MarketplaceJobTypes.CommonLabel, cancellationToken);
        var package = await db.ShipmentPackages.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == payload.PackageId && x.ConnectionId == connectionId, cancellationToken);
        var attempt = await db.ShipmentDocumentAttempts.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.IdempotencyKey == (job == null ? "" : job.EffectIdempotencyKey), cancellationToken);
        if (job is null || package is null || attempt is null) return JobExecutionResult.Blocked("COMMON_LABEL_STATE_MISSING", "Ortak etiket işi, paket veya deneme kaydı bulunamadı.");
        var platform = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId).Select(x => x.PlatformCode).SingleOrDefaultAsync(cancellationToken);
        var hepsiburada = platform == "HEPSIBURADA";
        var requestedFormat = (payload.Format ?? "ZPL").Trim().ToUpperInvariant();
        var validFormat = hepsiburada
            ? requestedFormat is "ZPL" or "BASE64ZPL" or "PDF" or "PNG" or "JPG"
            : requestedFormat == "ZPL";
        if (!validFormat) return JobExecutionResult.Blocked("COMMON_LABEL_FORMAT_UNSUPPORTED", "Ortak etiket işi desteklenmeyen biçim istiyor.");
        var labelLookupKey = hepsiburada ? package.ExternalPackageId : package.CargoTrackingNumber;
        if (string.IsNullOrWhiteSpace(labelLookupKey)) return JobExecutionResult.Blocked("CARGO_TRACKING_REQUIRED", "Etiket için paket veya kargo takip numarası gerekir.");
        if (hepsiburada ? !CommonLabelCarrierPolicy.SupportsHepsiburada(package.CargoProviderExternalId) : !CommonLabelCarrierPolicy.Supports(package.CargoProviderExternalId)) return JobExecutionResult.Blocked("COMMON_LABEL_CARRIER_UNSUPPORTED", hepsiburada ? "Hepsiburada ortak etiket yalnız HepsiJet ve Aras paketlerinde desteklenir." : "Ortak etiket yalnız Trendyol öder Aras Kargo veya TEX gönderilerinde kullanılabilir.");
        if (timeProvider.GetUtcNow() > payload.DeadlineAt) { attempt.Status = "MANUAL_REVIEW"; attempt.ErrorCode = "COMMON_LABEL_DEADLINE_EXPIRED"; attempt.CompletedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); return JobExecutionResult.ManualReview("COMMON_LABEL_DEADLINE_EXPIRED", "Ortak etiket belirlenen pencerede hazır olmadı."); }
        var phase = payload.Phase.Trim().ToUpperInvariant();
        if (phase == "SUBMIT")
        {
            if (hepsiburada)
            {
                var nextRead = payload with { Phase = "POLL" };
                job.PayloadJson = JsonSerializer.Serialize(nextRead);
                job.PayloadHash = Hash(job.PayloadJson);
                job.AvailableAt = timeProvider.GetUtcNow();
                attempt.Status = "POLLING";
                await db.SaveChangesAsync(cancellationToken);
                return JobExecutionResult.Retry("COMMON_LABEL_READ_PENDING", "Hepsiburada paket etiketi salt okunur API’den alınıyor.", TimeSpan.FromSeconds(1));
            }
            // Any record stops the send (open, unresolved or done).
            if (await effectGuard.ExistsAsync(tenantId, MarketplaceJobTypes.CommonLabel, job.EffectIdempotencyKey, cancellationToken)) return JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS", "Önceki ortak etiket oluşturma çağrısının sonucu kesinleştirilemedi.");
            var effect = effectGuard.New(tenantId, MarketplaceJobTypes.CommonLabel, job.EffectIdempotencyKey);
            var send = await effectGuard.SendAsync(effect, () =>
            {
                TrackRequest();
                return orders.CreateCommonLabelAsync(Context(tenantId, connectionId, correlationId, job.EffectIdempotencyKey), new(package.CargoTrackingNumber!, payload.BoxQuantity, payload.VolumetricHeight), cancellationToken);
            }, cancellationToken);
            if (!send.Accepted)
            {
                var error = send.Error;
                TrackResultFailure(error);
                if (send.Outcome == ExternalEffectSendOutcome.Uncertain) { attempt.Status = "MANUAL_REVIEW"; attempt.ErrorCode = "EXTERNAL_EFFECT_AMBIGUOUS"; attempt.CompletedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); return JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS", "Ortak etiket oluşturma çağrısının sonucu kesinleştirilemedi.", error.RemoteRequestId); }
                effectGuard.Release(effect);
                // Never sent (the panel's own circuit is open): the attempt stays open and the job runs again later.
                if (send.Outcome == ExternalEffectSendOutcome.LocalCircuit) { attempt.ErrorCode = error.Code; await db.SaveChangesAsync(cancellationToken); return JobExecutionResult.FromAdapterError(error); }
                // A quota answer fails the attempt like a refusal, while the job itself retries.
                attempt.Status = "FAILED"; attempt.ErrorCode = error.Code; attempt.CompletedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); return JobExecutionResult.FromAdapterError(error);
            }
            attempt.Status = "POLLING";
            var next = payload with { Phase = "POLL" }; job.PayloadJson = JsonSerializer.Serialize(next); job.PayloadHash = Hash(job.PayloadJson); await db.SaveChangesAsync(cancellationToken);
            return JobExecutionResult.Retry("COMMON_LABEL_PENDING", "Trendyol ortak etiket hazırlanıyor.", TimeSpan.FromSeconds(10));
        }
        if (phase != "POLL") return JobExecutionResult.Blocked("COMMON_LABEL_PHASE_INVALID", "Ortak etiket işi bilinmeyen bir fazda.");
        TrackRequest();
        var documentResult = await orders.GetCommonLabelAsync(Context(tenantId, connectionId, correlationId, $"{job.EffectIdempotencyKey}:poll"), labelLookupKey, cancellationToken, requestedFormat);
        if (!documentResult.IsSuccess)
        {
            TrackResultFailure(documentResult.Error);
            var error = documentResult.Error!;
            var mapped = JobExecutionResult.FromAdapterError(error);
            if (mapped.Kind == JobCompletionKind.Retry || error.Class == AdapterErrorClass.NotFound) return JobExecutionResult.Retry("COMMON_LABEL_PENDING", $"{(hepsiburada ? "Hepsiburada" : "Trendyol")} ortak etiketi henüz hazır değil.", TimeSpan.FromSeconds(20), error.RemoteRequestId);
            attempt.Status = mapped.Kind == JobCompletionKind.ManualReview ? "MANUAL_REVIEW" : "FAILED"; attempt.ErrorCode = error.Code; attempt.CompletedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); return mapped;
        }
        var document = documentResult.Value!;
        if (!string.Equals(document.Format, requestedFormat, StringComparison.OrdinalIgnoreCase) || document.Content.Length == 0 || document.Content.LongLength > 5 * 1024 * 1024)
        {
            attempt.Status = "MANUAL_REVIEW"; attempt.ErrorCode = "COMMON_LABEL_CONTRACT_INVALID"; attempt.CompletedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
            return JobExecutionResult.ManualReview("COMMON_LABEL_CONTRACT_INVALID", "Ortak etiket biçimi istenen biçimle eşleşmeli ve içerik 5 MiB altında olmalıdır.");
        }
        var (extension, mimeType) = requestedFormat switch
        {
            "BASE64ZPL" => ("zpl.base64", "text/plain"),
            "PDF" => ("pdf", "application/pdf"),
            "PNG" => ("png", "image/png"),
            "JPG" => ("jpg", "image/jpeg"),
            _ => ("zpl", "application/zpl")
        };
        var checksum = Convert.ToHexString(SHA256.HashData(document.Content));
        var existing = await db.ShipmentDocuments.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.PackageId == package.Id && x.DocumentKind == "COMMON_LABEL" && x.Format == requestedFormat && x.Checksum == checksum, cancellationToken);
        if (existing is null)
        {
            var safeLabelName = new string(labelLookupKey.Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_').Take(100).ToArray());
            if (string.IsNullOrWhiteSpace(safeLabelName)) safeLabelName = "package-label";
            var assetId = Guid.CreateVersion7(); await using var stream = new MemoryStream(document.Content, writable: false); var stored = await files.SaveAsync(tenantId, $"{assetId:N}-common-label.{extension}", mimeType, stream, 5 * 1024 * 1024, cancellationToken);
            var asset = new FileAsset { Id = assetId, TenantId = tenantId, Classification = "SHIPMENT_LABEL", RelativePath = stored, OriginalNameSafe = $"{safeLabelName}.{extension}", MimeType = mimeType, SizeBytes = document.Content.LongLength, Sha256 = checksum, Status = "ACTIVE", CreatedAt = timeProvider.GetUtcNow() };
            db.FileAssets.Add(asset);
            var version = await db.ShipmentDocuments.Where(x => x.TenantId == tenantId && x.PackageId == package.Id && x.DocumentKind == "COMMON_LABEL").Select(x => (int?)x.DocumentVersion).MaxAsync(cancellationToken) ?? 0;
            existing = new ShipmentDocument { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, PackageId = package.Id, FileAssetId = assetId, DocumentKind = "COMMON_LABEL", Format = requestedFormat, Source = hepsiburada ? "HEPSIBURADA" : "TRENDYOL", Checksum = checksum, DocumentVersion = version + 1, CreatedAt = timeProvider.GetUtcNow() };
            db.ShipmentDocuments.Add(existing);
        }
        attempt.DocumentId = existing.Id; attempt.Status = "SUCCEEDED"; attempt.ErrorCode = null; attempt.CompletedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); return JobExecutionResult.Success();
    }
}
