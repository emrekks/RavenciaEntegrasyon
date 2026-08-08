using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class F3JobProcessor(AppDbContext db, IConnectionPort connections, IReferenceDataPort references, IProductPort products, IInventoryPricePort inventoryPrice, IOrderPort orders, IReturnPort returns, IPrivateFileStorage files, TimeProvider timeProvider) : IF3JobProcessor
{
    public async Task<JobExecutionResult> ProcessAsync(Guid tenantId, Guid? connectionId, string jobType, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        if (connectionId is null) return JobExecutionResult.Blocked("CONNECTION_REQUIRED", "Job requires a platform connection.");
        var platform = await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId.Value).Select(x => x.PlatformCode).SingleOrDefaultAsync(cancellationToken);
        if (!ActiveIntegrationScope.Contains(platform)) return JobExecutionResult.Blocked("CONNECTION_OUT_OF_SCOPE", "Connection is not active in the current integration scope.");
        try
        {
            if (jobType == F3JobTypes.ProductCreate) return await CreateProduct(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken);
            if (jobType == F3JobTypes.ProductApprovalReconcile) return await ReconcileProductApproval(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken);
            if (jobType == F3JobTypes.ProductUpdate) return await UpdateProduct(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken);
            if (jobType == F3JobTypes.ProductArchive) return await ArchiveProduct(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken);
            if (jobType == F3JobTypes.PriceInventorySync) return await SyncPriceInventory(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken);
            if (jobType == F3JobTypes.CommonLabel) return await CommonLabel(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken);
            var succeeded = jobType switch
            {
                F3JobTypes.ConnectionTest => await TestConnection(tenantId, connectionId.Value, correlationId, cancellationToken),
                F3JobTypes.ReferenceSync => await SyncReferences(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken),
                F3JobTypes.OrderSync => await SyncOrders(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken),
                F3JobTypes.ReturnSync => await SyncReturns(tenantId, connectionId.Value, correlationId, cancellationToken),
                F3JobTypes.WebhookIngest => await IngestWebhook(tenantId, connectionId.Value, payloadJson, cancellationToken),
                F3JobTypes.ShipmentAction => await ShipmentAction(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken),
                F3JobTypes.ReturnAction => await ReturnAction(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken),
                _ => false
            };
            return succeeded ? JobExecutionResult.Success() : JobExecutionResult.Blocked("F3_JOB_REJECTED", "Job payload, capability or current entity state did not permit the operation.");
        }
        catch (JobProcessingException exception)
        {
            return exception.Result;
        }
    }

    private async Task<JobExecutionResult> CreateProduct(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        ProductPublicationJobPayload? payload;
        try { payload = JsonSerializer.Deserialize<ProductPublicationJobPayload>(payloadJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (JsonException) { return JobExecutionResult.Blocked("PRODUCT_PUBLICATION_PAYLOAD_INVALID", "Ürün yayınlama işi payload sözleşmesini sağlamıyor."); }
        if (payload is null || payload.JobId == Guid.Empty || payload.ProductId == Guid.Empty || payload.ProfileId == Guid.Empty || string.IsNullOrWhiteSpace(payload.PayloadHash) || string.IsNullOrWhiteSpace(payload.PayloadJson)) return JobExecutionResult.Blocked("PRODUCT_PUBLICATION_PAYLOAD_INVALID", "Ürün yayınlama işi zorunlu alanları eksik.");

        var job = await db.IntegrationJobs.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == payload.JobId && x.ConnectionId == connectionId && x.JobType == F3JobTypes.ProductCreate, cancellationToken);
        var profile = await db.ChannelListingProfiles.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == payload.ProfileId && x.ProductId == payload.ProductId && x.ConnectionId == connectionId, cancellationToken);
        if (job is null || profile is null) return JobExecutionResult.Blocked("PRODUCT_PUBLICATION_STATE_MISSING", "Yayın işi veya listing profile bulunamadı.");

        if (string.Equals(payload.Phase, "SUBMIT", StringComparison.OrdinalIgnoreCase))
        {
            var existingEffect = await db.ExternalEffectRecords.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.EffectType == F3JobTypes.ProductCreate && x.IdempotencyKey == job.EffectIdempotencyKey, cancellationToken);
            if (existingEffect is not null) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "EXTERNAL_EFFECT_AMBIGUOUS", JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS", "Önceki dış yazmanın sonucu kesinleştirilemedi; tekrar gönderim engellendi."), cancellationToken);

            var effect = new ExternalEffectRecord { Id = Guid.CreateVersion7(), TenantId = tenantId, EffectType = F3JobTypes.ProductCreate, IdempotencyKey = job.EffectIdempotencyKey, CreatedAt = timeProvider.GetUtcNow() };
            db.ExternalEffectRecords.Add(effect);
            await db.SaveChangesAsync(cancellationToken);

            var submit = await products.CreateAsync(Context(tenantId, connectionId, correlationId, job.EffectIdempotencyKey), new ProductPublication(payload.ProductId, payload.PayloadHash, payload.PayloadJson), cancellationToken);
            if (!submit.IsSuccess)
            {
                var error = submit.Error!;
                if (error.Class is AdapterErrorClass.TransientNetwork or AdapterErrorClass.Remote5xx or AdapterErrorClass.ContractViolation or AdapterErrorClass.InternalBug)
                    return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "EXTERNAL_EFFECT_AMBIGUOUS", JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS", "Create çağrısının uzak tarafta uygulanıp uygulanmadığı kesinleştirilemedi.", error.RemoteRequestId), cancellationToken);
                db.ExternalEffectRecords.Remove(effect);
                await db.SaveChangesAsync(cancellationToken);
                var adapterResult = JobExecutionResult.FromAdapterError(error);
                var status = adapterResult.Kind == JobCompletionKind.Retry ? "RETRY_SCHEDULED" : adapterResult.Kind == JobCompletionKind.ManualReview ? "MANUAL_REVIEW" : "BLOCKED";
                return await MarkPublicationResult(tenantId, connectionId, profile, status, error.Code, adapterResult, cancellationToken);
            }

            var operation = submit.Value!;
            effect.CompletedAt = timeProvider.GetUtcNow();
            var next = payload with { Phase = "POLL", ExternalOperationId = operation.ExternalOperationId, SubmittedAt = operation.SubmittedAt };
            job.PayloadJson = JsonSerializer.Serialize(next);
            job.PayloadHash = Hash(job.PayloadJson);
            profile.ActualStatus = "BATCH_SUBMITTED";
            profile.LastRejectionCode = null;
            profile.Version++;
            await SetListingStatus(tenantId, connectionId, profile.Id, "BATCH_SUBMITTED", null, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return JobExecutionResult.Retry("PRODUCT_BATCH_PENDING", "Trendyol create batch sonucu bekleniyor.", TimeSpan.FromSeconds(15), operation.ExternalOperationId);
        }

        if (!string.Equals(payload.Phase, "POLL", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(payload.ExternalOperationId) || payload.SubmittedAt is null) return JobExecutionResult.Blocked("PRODUCT_PUBLICATION_PHASE_INVALID", "Yayın işi bilinmeyen bir fazda.");
        if (timeProvider.GetUtcNow() - payload.SubmittedAt.Value > TimeSpan.FromHours(4)) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_BATCH_RESULT_EXPIRED", JobExecutionResult.ManualReview("PRODUCT_BATCH_RESULT_EXPIRED", "Batch sonucu dört saatlik sorgulama penceresinde tamamlanamadı.", payload.ExternalOperationId), cancellationToken);

        var operationResult = await products.GetOperationAsync(Context(tenantId, connectionId, correlationId, $"{job.EffectIdempotencyKey}:poll"), payload.ExternalOperationId, cancellationToken);
        if (!operationResult.IsSuccess)
        {
            var adapterResult = JobExecutionResult.FromAdapterError(operationResult.Error!);
            if (adapterResult.Kind == JobCompletionKind.Retry) return adapterResult;
            var status = adapterResult.Kind == JobCompletionKind.ManualReview ? "MANUAL_REVIEW" : "BLOCKED";
            return await MarkPublicationResult(tenantId, connectionId, profile, status, operationResult.Error!.Code, adapterResult, cancellationToken);
        }

        var operationStatus = operationResult.Value!;
        if (string.Equals(operationStatus.Status, "IN_PROGRESS", StringComparison.OrdinalIgnoreCase))
        {
            profile.ActualStatus = "BATCH_IN_PROGRESS";
            profile.Version++;
            await SetListingStatus(tenantId, connectionId, profile.Id, "BATCH_IN_PROGRESS", null, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return JobExecutionResult.Retry("PRODUCT_BATCH_PENDING", "Trendyol create batch işlemi sürüyor.", TimeSpan.FromSeconds(15), payload.ExternalOperationId);
        }
        if (!string.Equals(operationStatus.Status, "COMPLETED", StringComparison.OrdinalIgnoreCase)) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_BATCH_STATUS_UNKNOWN", JobExecutionResult.ManualReview("PRODUCT_BATCH_STATUS_UNKNOWN", "Batch servisi tanınmayan bir durum döndürdü.", payload.ExternalOperationId), cancellationToken);

        var listings = await db.ChannelListingVariants.Where(x => x.TenantId == tenantId && x.ProfileId == profile.Id).ToListAsync(cancellationToken);
        var listingVariantIds = listings.Select(x => x.VariantId).ToArray();
        var states = await db.MarketplaceListingStates.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && listingVariantIds.Contains(x.VariantId)).ToDictionaryAsync(x => x.VariantId, cancellationToken);
        var lines = operationStatus.Lines.Where(x => !string.IsNullOrWhiteSpace(x.ExternalKey)).ToList();
        if (lines.Count == 0 || lines.GroupBy(x => x.ExternalKey, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1)) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_BATCH_CONTRACT_INVALID", JobExecutionResult.ManualReview("PRODUCT_BATCH_CONTRACT_INVALID", "Tamamlanan batch sonucu eksik veya yinelenen barkod satırları içeriyor.", payload.ExternalOperationId), cancellationToken);
        var byBarcode = lines.ToDictionary(x => x.ExternalKey, StringComparer.OrdinalIgnoreCase);
        if (listings.Any(x => string.IsNullOrWhiteSpace(x.ExternalBarcode) || !byBarcode.ContainsKey(x.ExternalBarcode))) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_BATCH_CONTRACT_INVALID", JobExecutionResult.ManualReview("PRODUCT_BATCH_CONTRACT_INVALID", "Batch sonucu gönderilen tüm barkodları içermiyor.", payload.ExternalOperationId), cancellationToken);
        if (byBarcode.Keys.Any(key => listings.All(x => !string.Equals(x.ExternalBarcode, key, StringComparison.OrdinalIgnoreCase)))) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_BATCH_CONTRACT_INVALID", JobExecutionResult.ManualReview("PRODUCT_BATCH_CONTRACT_INVALID", "Batch sonucu bilinmeyen barkod içeriyor.", payload.ExternalOperationId), cancellationToken);

        var succeeded = 0;
        foreach (var listing in listings)
        {
            var line = byBarcode[listing.ExternalBarcode!];
            var rejection = line.Succeeded ? null : SafeCode(line.ErrorCode ?? "REMOTE_VALIDATION_FAILED");
            listing.ActualStatus = line.Succeeded ? "CREATE_ACCEPTED" : "CREATE_REJECTED";
            listing.RejectionCode = rejection;
            if (line.Succeeded) succeeded++;
            if (states.TryGetValue(listing.VariantId, out var state))
            {
                state.ActualStatus = listing.ActualStatus;
                state.LastRejectionCode = rejection;
                state.Version++;
            }
        }
        profile.ActualStatus = succeeded == listings.Count ? "APPROVAL_PENDING" : succeeded == 0 ? "CREATE_REJECTED" : "PARTIAL_FAILURE";
        profile.LastRejectionCode = listings.Select(x => x.RejectionCode).FirstOrDefault(x => x is not null);
        profile.Version++;
        if (succeeded > 0)
            await EnsureApprovalReconciliationJob(tenantId, connectionId, payload.ProductId, profile.Id, payload.PayloadHash, correlationId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        if (succeeded == listings.Count) return JobExecutionResult.Success();
        return succeeded == 0
            ? JobExecutionResult.Blocked("PRODUCT_BATCH_REJECTED", "Trendyol create batch içindeki tüm varyantlar reddedildi.", payload.ExternalOperationId)
            : JobExecutionResult.Blocked("PRODUCT_BATCH_PARTIAL_FAILURE", "Trendyol create batch kısmi başarısızlıkla tamamlandı.", payload.ExternalOperationId);
    }

    private async Task<JobExecutionResult> ReconcileProductApproval(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        ProductApprovalReconciliationJobPayload? payload;
        try { payload = JsonSerializer.Deserialize<ProductApprovalReconciliationJobPayload>(payloadJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (JsonException) { return JobExecutionResult.Blocked("PRODUCT_APPROVAL_PAYLOAD_INVALID", "Ürün onay uzlaştırma işi payload sözleşmesini sağlamıyor."); }
        if (payload is null || payload.JobId == Guid.Empty || payload.ProductId == Guid.Empty || payload.ProfileId == Guid.Empty || string.IsNullOrWhiteSpace(payload.PayloadHash) || payload.DeadlineAt <= payload.StartedAt)
            return JobExecutionResult.Blocked("PRODUCT_APPROVAL_PAYLOAD_INVALID", "Ürün onay uzlaştırma işi zorunlu alanları eksik.");

        var job = await db.IntegrationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == payload.JobId && x.ConnectionId == connectionId && x.JobType == F3JobTypes.ProductApprovalReconcile, cancellationToken);
        var profile = await db.ChannelListingProfiles.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == payload.ProfileId && x.ProductId == payload.ProductId && x.ConnectionId == connectionId, cancellationToken);
        if (job is null || profile is null) return JobExecutionResult.Blocked("PRODUCT_APPROVAL_STATE_MISSING", "Onay uzlaştırma işi veya listing profile bulunamadı.");

        var listings = await db.ChannelListingVariants.Where(x => x.TenantId == tenantId && x.ProfileId == profile.Id).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        var allVariantIds = listings.Select(x => x.VariantId).ToArray();
        var states = allVariantIds.Length == 0
            ? new Dictionary<Guid, MarketplaceListingState>()
            : await db.MarketplaceListingStates.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && allVariantIds.Contains(x.VariantId)).ToDictionaryAsync(x => x.VariantId, cancellationToken);
        if (states.Values.Any(x => !string.Equals(x.PayloadHash, payload.PayloadHash, StringComparison.Ordinal)))
            return JobExecutionResult.Blocked("PRODUCT_APPROVAL_SUPERSEDED", "Daha yeni bir ürün yayınlama payload'ı bulundu; eski onay işi güncel listing durumunu değiştirmedi.");
        if (timeProvider.GetUtcNow() > payload.DeadlineAt)
            return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_APPROVAL_DEADLINE_EXPIRED", JobExecutionResult.ManualReview("PRODUCT_APPROVAL_DEADLINE_EXPIRED", "Trendyol ürün onayı belirlenen uzlaştırma penceresinde terminal duruma ulaşmadı."), cancellationToken);
        if (listings.Count == 0 || states.Count != listings.Count)
            return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_APPROVAL_STATE_INCOMPLETE", JobExecutionResult.ManualReview("PRODUCT_APPROVAL_STATE_INCOMPLETE", "Onay uzlaştırması için listing state kayıtları eksik."), cancellationToken);

        var candidates = listings.Where(x => !string.Equals(x.ActualStatus, "CREATE_REJECTED", StringComparison.Ordinal) && !string.Equals(x.ActualStatus, "UPDATE_REJECTED", StringComparison.Ordinal)).ToList();
        if (candidates.Count == 0 || candidates.Any(x => string.IsNullOrWhiteSpace(x.ExternalBarcode)) || candidates.Select(x => x.ExternalBarcode).Distinct(StringComparer.OrdinalIgnoreCase).Count() != candidates.Count)
            return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_APPROVAL_BARCODES_INVALID", JobExecutionResult.ManualReview("PRODUCT_APPROVAL_BARCODES_INVALID", "Onay uzlaştırması için kabul edilmiş ve benzersiz barkod listesi bulunamadı."), cancellationToken);

        var remoteByBarcode = new Dictionary<string, RemotePublicationStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var listing in candidates)
        {
            var barcode = listing.ExternalBarcode!;
            var result = await products.GetPublicationStatusAsync(Context(tenantId, connectionId, correlationId, $"product-approval:{profile.Id:N}:{barcode}"), barcode, cancellationToken);
            if (!result.IsSuccess)
            {
                var adapterResult = JobExecutionResult.FromAdapterError(result.Error!);
                if (adapterResult.Kind == JobCompletionKind.Retry) return adapterResult;
                return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", result.Error!.Code, JobExecutionResult.ManualReview(result.Error.Code, result.Error.SafeMessage, result.Error.RemoteRequestId), cancellationToken);
            }
            var remoteStatus = result.Value!;
            if (!string.Equals(remoteStatus.Barcode, barcode, StringComparison.OrdinalIgnoreCase))
                return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_APPROVAL_CONTRACT_INVALID", JobExecutionResult.ManualReview("PRODUCT_APPROVAL_CONTRACT_INVALID", "Trendyol ürün durum yanıtı istenen barkodla eşleşmedi."), cancellationToken);
            remoteByBarcode.Add(barcode, remoteStatus);
        }

        var localVariantIds = candidates.Select(x => x.VariantId).ToArray();
        var approvedStatuses = remoteByBarcode.Values.Where(x => x.Status == "APPROVED").ToList();
        if (approvedStatuses.Any(x => string.IsNullOrWhiteSpace(x.ExternalProductId) || string.IsNullOrWhiteSpace(x.ExternalVariantId)))
            return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_APPROVAL_IDENTIFIERS_MISSING", JobExecutionResult.ManualReview("PRODUCT_APPROVAL_IDENTIFIERS_MISSING", "Onaylanan ürün yanıtında contentId veya variantId bulunamadı."), cancellationToken);
        var approvedContentIds = approvedStatuses.Select(x => x.ExternalProductId!).Distinct(StringComparer.Ordinal).ToList();
        if (approvedContentIds.Count > 1)
            return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_APPROVAL_CONTENT_SPLIT", JobExecutionResult.ManualReview("PRODUCT_APPROVAL_CONTENT_SPLIT", "Tek yerel ürünün varyantları birden fazla Trendyol content kimliğine ayrılmış görünüyor."), cancellationToken);
        var approvedVariantIds = approvedStatuses.Select(x => x.ExternalVariantId!).ToList();
        if (approvedVariantIds.Distinct(StringComparer.Ordinal).Count() != approvedVariantIds.Count)
            return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_APPROVAL_VARIANT_ID_DUPLICATE", JobExecutionResult.ManualReview("PRODUCT_APPROVAL_VARIANT_ID_DUPLICATE", "Trendyol onay yanıtı birden fazla barkod için aynı variantId değerini döndürdü."), cancellationToken);
        if (approvedContentIds.Count == 1)
        {
            var approvedContentId = approvedContentIds[0];
            if (await db.MarketplaceProductLinks.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalId == approvedContentId && x.ProductId != payload.ProductId, cancellationToken))
                return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_APPROVAL_IDENTITY_CONFLICT", JobExecutionResult.ManualReview("PRODUCT_APPROVAL_IDENTITY_CONFLICT", "Trendyol content kimliği başka bir yerel ürünle eşleşiyor."), cancellationToken);
            var localProductLink = await db.MarketplaceProductLinks.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ProductId == payload.ProductId, cancellationToken);
            if (localProductLink is not null && !string.Equals(localProductLink.ExternalId, approvedContentId, StringComparison.Ordinal))
                return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_APPROVAL_IDENTITY_CONFLICT", JobExecutionResult.ManualReview("PRODUCT_APPROVAL_IDENTITY_CONFLICT", "Yerel ürün daha önce farklı bir Trendyol content kimliğiyle eşleştirilmiş."), cancellationToken);
        }
        if (approvedVariantIds.Count > 0 && await db.MarketplaceVariantLinks.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && approvedVariantIds.Contains(x.ExternalId) && !localVariantIds.Contains(x.VariantId), cancellationToken))
            return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_APPROVAL_IDENTITY_CONFLICT", JobExecutionResult.ManualReview("PRODUCT_APPROVAL_IDENTITY_CONFLICT", "Trendyol variant kimliği başka bir yerel varyantla eşleşiyor."), cancellationToken);
        var approvedVariantByLocalId = candidates
            .Where(listing => remoteByBarcode[listing.ExternalBarcode!].Status == "APPROVED")
            .ToDictionary(listing => listing.VariantId, listing => remoteByBarcode[listing.ExternalBarcode!].ExternalVariantId!, EqualityComparer<Guid>.Default);
        var localVariantLinks = await db.MarketplaceVariantLinks.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && localVariantIds.Contains(x.VariantId)).ToListAsync(cancellationToken);
        if (localVariantLinks.Any(link => approvedVariantByLocalId.TryGetValue(link.VariantId, out var expectedExternalId) && !string.Equals(link.ExternalId, expectedExternalId, StringComparison.Ordinal)))
            return await MarkApprovalResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_APPROVAL_IDENTITY_CONFLICT", JobExecutionResult.ManualReview("PRODUCT_APPROVAL_IDENTITY_CONFLICT", "Yerel varyant daha önce farklı bir Trendyol variant kimliğiyle eşleştirilmiş."), cancellationToken);

        var live = 0;
        var rejected = listings.Count - candidates.Count;
        var pending = 0;
        var exceptional = 0;
        string? firstCode = listings.Where(x => string.Equals(x.ActualStatus, "CREATE_REJECTED", StringComparison.Ordinal) || string.Equals(x.ActualStatus, "UPDATE_REJECTED", StringComparison.Ordinal)).Select(x => x.RejectionCode).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (approvedContentIds.Count == 1)
            await UpsertMarketplaceProductLink(tenantId, connectionId, payload.ProductId, approvedContentIds[0], cancellationToken);
        foreach (var listing in candidates)
        {
            var remote = remoteByBarcode[listing.ExternalBarcode!];
            var localStatus = remote.Status switch
            {
                "APPROVED" => "LIVE",
                "PENDING_APPROVAL" or "NOT_FOUND" => "APPROVAL_PENDING",
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
                    await UpsertMarketplaceVariantLink(tenantId, connectionId, listing.VariantId, remote.ExternalVariantId!, cancellationToken);
                    live++;
                    break;
                case "REJECTED": rejected++; firstCode ??= code; break;
                case "APPROVAL_PENDING": pending++; break;
                default: exceptional++; firstCode ??= code; break;
            }
        }

        profile.DesiredStatus = "LIVE";
        if (exceptional > 0)
        {
            profile.ActualStatus = "MANUAL_REVIEW";
            profile.LastRejectionCode = firstCode;
        }
        else if (pending > 0)
        {
            profile.ActualStatus = live + rejected > 0 ? "APPROVAL_PARTIAL_PENDING" : "APPROVAL_PENDING";
            profile.LastRejectionCode = firstCode;
        }
        else if (live == listings.Count)
        {
            profile.ActualStatus = "LIVE";
            profile.LastRejectionCode = null;
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
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (exceptional > 0) return JobExecutionResult.ManualReview(firstCode ?? "PRODUCT_APPROVAL_REVIEW_REQUIRED", "Trendyol ürün onayında operatör incelemesi gerektiren terminal durum oluştu.");
        if (pending > 0) return JobExecutionResult.Retry("PRODUCT_APPROVAL_PENDING", "Trendyol ürün onayı henüz tamamlanmadı.", TimeSpan.FromMinutes(5));
        if (live == listings.Count) return JobExecutionResult.Success();
        return rejected == listings.Count
            ? JobExecutionResult.Blocked("PRODUCT_APPROVAL_REJECTED", "Trendyol ürün onayı tüm varyantlar için reddedildi.")
            : JobExecutionResult.Blocked("PRODUCT_APPROVAL_PARTIAL_REJECTION", "Trendyol ürün onayı bazı varyantlar için reddedildi.");
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
            listing.ActualStatus = status;
            listing.RejectionCode = SafeCode(rejectionCode);
            if (states.TryGetValue(listing.VariantId, out var state))
            {
                state.ActualStatus = status;
                state.LastRejectionCode = SafeCode(rejectionCode);
                state.Version++;
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        return result;
    }

    private async Task EnsureApprovalReconciliationJob(Guid tenantId, Guid connectionId, Guid productId, Guid profileId, string payloadHash, string correlationId, CancellationToken cancellationToken)
    {
        var dedup = $"product-approval:{connectionId:N}:{profileId:N}:{payloadHash}";
        if (await db.IntegrationJobs.AnyAsync(x => x.TenantId == tenantId && x.JobType == F3JobTypes.ProductApprovalReconcile && x.JobDedupKey == dedup, cancellationToken)) return;
        var now = timeProvider.GetUtcNow();
        var jobId = Guid.CreateVersion7();
        var payload = JsonSerializer.Serialize(new ProductApprovalReconciliationJobPayload(jobId, productId, profileId, payloadHash, now, now.AddDays(7)));
        db.IntegrationJobs.Add(new IntegrationJob
        {
            Id = jobId,
            TenantId = tenantId,
            ConnectionId = connectionId,
            JobType = F3JobTypes.ProductApprovalReconcile,
            PayloadJson = payload,
            PayloadVersion = 1,
            PayloadHash = Hash(payload),
            JobDedupKey = dedup,
            EffectIdempotencyKey = dedup,
            Priority = 1,
            Status = JobStatus.Pending,
            AvailableAt = now,
            MaxAttempts = 200,
            CorrelationId = correlationId,
            CreatedAt = now,
            Version = 1
        });
    }

    private async Task UpsertMarketplaceProductLink(Guid tenantId, Guid connectionId, Guid productId, string externalProductId, CancellationToken cancellationToken)
    {
        var productLink = await db.MarketplaceProductLinks.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ProductId == productId, cancellationToken);
        if (productLink is null) db.MarketplaceProductLinks.Add(new MarketplaceProductLink { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, ProductId = productId, ExternalId = externalProductId, Version = 1 });
    }

    private async Task UpsertMarketplaceVariantLink(Guid tenantId, Guid connectionId, Guid variantId, string externalVariantId, CancellationToken cancellationToken)
    {
        var variantLink = await db.MarketplaceVariantLinks.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.VariantId == variantId, cancellationToken);
        if (variantLink is null) db.MarketplaceVariantLinks.Add(new MarketplaceVariantLink { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, VariantId = variantId, ExternalId = externalVariantId, Version = 1 });
    }

    private async Task<JobExecutionResult> MarkPublicationResult(Guid tenantId, Guid connectionId, ChannelListingProfile profile, string status, string? rejectionCode, JobExecutionResult result, CancellationToken cancellationToken)
    {
        profile.ActualStatus = status;
        profile.LastRejectionCode = SafeCode(rejectionCode);
        profile.Version++;
        await SetListingStatus(tenantId, connectionId, profile.Id, status, rejectionCode, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return result;
    }

    private async Task SetListingStatus(Guid tenantId, Guid connectionId, Guid profileId, string status, string? rejectionCode, CancellationToken cancellationToken)
    {
        var listings = await db.ChannelListingVariants.Where(x => x.TenantId == tenantId && x.ProfileId == profileId).ToListAsync(cancellationToken);
        var variantIds = listings.Select(x => x.VariantId).ToArray();
        var states = await db.MarketplaceListingStates.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && variantIds.Contains(x.VariantId)).ToDictionaryAsync(x => x.VariantId, cancellationToken);
        foreach (var listing in listings)
        {
            listing.ActualStatus = status;
            listing.RejectionCode = SafeCode(rejectionCode);
            if (states.TryGetValue(listing.VariantId, out var state)) { state.ActualStatus = status; state.LastRejectionCode = SafeCode(rejectionCode); state.Version++; }
        }
    }

    private static string? SafeCode(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(256, value.Trim().Length)];

    private async Task<JobExecutionResult> UpdateProduct(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        ProductUpdateJobPayload? payload;
        try { payload = JsonSerializer.Deserialize<ProductUpdateJobPayload>(payloadJson, JsonOptions); }
        catch (JsonException) { return JobExecutionResult.Blocked("PRODUCT_UPDATE_PAYLOAD_INVALID", "Ürün güncelleme işi payload sözleşmesini sağlamıyor."); }
        if (payload is null || payload.JobId == Guid.Empty || payload.ProductId == Guid.Empty || payload.ProfileId == Guid.Empty || string.IsNullOrWhiteSpace(payload.PayloadHash))
            return JobExecutionResult.Blocked("PRODUCT_UPDATE_PAYLOAD_INVALID", "Ürün güncelleme işi zorunlu alanları eksik.");

        var job = await db.IntegrationJobs.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == payload.JobId && x.ConnectionId == connectionId && x.JobType == F3JobTypes.ProductUpdate, cancellationToken);
        var profile = await db.ChannelListingProfiles.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == payload.ProfileId && x.ProductId == payload.ProductId && x.ConnectionId == connectionId, cancellationToken);
        if (job is null || profile is null) return JobExecutionResult.Blocked("PRODUCT_UPDATE_STATE_MISSING", "Ürün güncelleme işi veya listing profile bulunamadı.");

        var listings = await db.ChannelListingVariants.Where(x => x.TenantId == tenantId && x.ProfileId == profile.Id).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        var variantIds = listings.Select(x => x.VariantId).ToArray();
        var states = variantIds.Length == 0 ? new Dictionary<Guid, MarketplaceListingState>() : await db.MarketplaceListingStates.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && variantIds.Contains(x.VariantId)).ToDictionaryAsync(x => x.VariantId, cancellationToken);
        if (listings.Count == 0 || states.Count != listings.Count) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_UPDATE_STATE_INCOMPLETE", JobExecutionResult.ManualReview("PRODUCT_UPDATE_STATE_INCOMPLETE", "Ürün güncelleme listing state kayıtları eksik."), cancellationToken);
        if (states.Values.Any(x => !string.Equals(x.PayloadHash, payload.PayloadHash, StringComparison.Ordinal))) return JobExecutionResult.Blocked("PRODUCT_UPDATE_SUPERSEDED", "Daha yeni ürün payload'ı bulundu; eski güncelleme işi uzak çağrı yapmadan durduruldu.");

        var phase = payload.Phase.Trim().ToUpperInvariant();
        if (phase.StartsWith("SUBMIT_", StringComparison.Ordinal))
        {
            var phasePayload = UpdatePayload(payload, phase);
            if (!HasItems(phasePayload))
            {
                var skipped = AdvanceUpdate(payload, phase);
                if (skipped is null) return await CompleteProductUpdate(tenantId, connectionId, payload, profile, listings, states, correlationId, cancellationToken);
                job.PayloadJson = JsonSerializer.Serialize(skipped); job.PayloadHash = Hash(job.PayloadJson);
                await db.SaveChangesAsync(cancellationToken);
                return JobExecutionResult.Retry("PRODUCT_UPDATE_NEXT_PHASE", "Boş ürün güncelleme fazı atlandı.", TimeSpan.FromSeconds(1));
            }

            var effectType = $"{F3JobTypes.ProductUpdate}:{phase}";
            var effectKey = $"{job.EffectIdempotencyKey}:{phase}";
            if (await db.ExternalEffectRecords.AnyAsync(x => x.TenantId == tenantId && x.EffectType == effectType && x.IdempotencyKey == effectKey, cancellationToken))
                return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "EXTERNAL_EFFECT_AMBIGUOUS", JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS", "Önceki ürün güncelleme çağrısının sonucu kesinleştirilemedi; tekrar gönderim engellendi."), cancellationToken);

            var effect = new ExternalEffectRecord { Id = Guid.CreateVersion7(), TenantId = tenantId, EffectType = effectType, IdempotencyKey = effectKey, CreatedAt = timeProvider.GetUtcNow() };
            db.ExternalEffectRecords.Add(effect); await db.SaveChangesAsync(cancellationToken);
            var publication = new ProductUpdatePublication(payload.ProductId, payload.Mode, payload.PayloadHash, payload.UnapprovedPayloadJson, payload.ApprovedContentPayloadJson, payload.ApprovedVariantPayloadJson, payload.ApprovedDeliveryPayloadJson);
            var context = Context(tenantId, connectionId, correlationId, effectKey);
            var submit = phase switch
            {
                "SUBMIT_UNAPPROVED" => await products.UpdateUnapprovedAsync(context, publication, cancellationToken),
                "SUBMIT_CONTENT" => await products.UpdateApprovedContentAsync(context, publication, cancellationToken),
                "SUBMIT_VARIANTS" => await products.UpdateApprovedVariantsAsync(context, publication, cancellationToken),
                "SUBMIT_DELIVERY" => await products.UpdateApprovedDeliveryAsync(context, publication, cancellationToken),
                _ => AdapterResult<RemoteOperationRef>.Failure(new(AdapterErrorClass.ContractViolation, "PRODUCT_UPDATE_PHASE_INVALID", "Ürün güncelleme fazı tanınmıyor.", null, null, null))
            };
            if (!submit.IsSuccess)
            {
                var error = submit.Error!;
                if (IsAmbiguous(error)) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "EXTERNAL_EFFECT_AMBIGUOUS", JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS", "Ürün güncelleme çağrısının uygulanıp uygulanmadığı kesinleştirilemedi.", error.RemoteRequestId), cancellationToken);
                db.ExternalEffectRecords.Remove(effect); await db.SaveChangesAsync(cancellationToken);
                return await MarkPublicationResult(tenantId, connectionId, profile, "UPDATE_BLOCKED", error.Code, JobExecutionResult.FromAdapterError(error), cancellationToken);
            }
            effect.CompletedAt = timeProvider.GetUtcNow();
            var operation = submit.Value!;
            var poll = payload with { Phase = phase.Replace("SUBMIT_", "POLL_", StringComparison.Ordinal), ExternalOperationId = operation.ExternalOperationId, SubmittedAt = operation.SubmittedAt };
            job.PayloadJson = JsonSerializer.Serialize(poll); job.PayloadHash = Hash(job.PayloadJson);
            profile.ActualStatus = phase.Replace("SUBMIT_", "UPDATE_", StringComparison.Ordinal) + "_SUBMITTED"; profile.Version++;
            await db.SaveChangesAsync(cancellationToken);
            return JobExecutionResult.Retry("PRODUCT_UPDATE_BATCH_PENDING", "Trendyol ürün güncelleme batch sonucu bekleniyor.", TimeSpan.FromSeconds(15), operation.ExternalOperationId);
        }

        if (!phase.StartsWith("POLL_", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(payload.ExternalOperationId) || payload.SubmittedAt is null)
            return JobExecutionResult.Blocked("PRODUCT_UPDATE_PHASE_INVALID", "Ürün güncelleme işi bilinmeyen bir fazda.");
        if (timeProvider.GetUtcNow() - payload.SubmittedAt.Value > TimeSpan.FromHours(4))
            return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_UPDATE_BATCH_EXPIRED", JobExecutionResult.ManualReview("PRODUCT_UPDATE_BATCH_EXPIRED", "Ürün güncelleme batch sonucu dört saatlik pencerede alınamadı.", payload.ExternalOperationId), cancellationToken);

        var operationResult = await products.GetOperationAsync(Context(tenantId, connectionId, correlationId, $"{job.EffectIdempotencyKey}:{phase}:poll"), payload.ExternalOperationId, cancellationToken);
        if (!operationResult.IsSuccess)
        {
            var result = JobExecutionResult.FromAdapterError(operationResult.Error!);
            return result.Kind == JobCompletionKind.Retry ? result : await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", operationResult.Error!.Code, result, cancellationToken);
        }
        var status = operationResult.Value!;
        if (string.Equals(status.Status, "IN_PROGRESS", StringComparison.OrdinalIgnoreCase)) return JobExecutionResult.Retry("PRODUCT_UPDATE_BATCH_PENDING", "Trendyol ürün güncelleme batch sonucu bekleniyor.", TimeSpan.FromSeconds(20), payload.ExternalOperationId);
        if (!string.Equals(status.Status, "COMPLETED", StringComparison.OrdinalIgnoreCase)) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_UPDATE_BATCH_STATUS_UNKNOWN", JobExecutionResult.ManualReview("PRODUCT_UPDATE_BATCH_STATUS_UNKNOWN", "Ürün güncelleme batch servisi tanınmayan durum döndürdü.", payload.ExternalOperationId), cancellationToken);

        var failed = status.Lines.Where(x => !x.Succeeded).ToList();
        if (phase == "POLL_CONTENT" && failed.Count > 0)
            return await MarkPublicationResult(tenantId, connectionId, profile, "UPDATE_REJECTED", SafeCode(failed[0].ErrorCode) ?? "PRODUCT_UPDATE_CONTENT_REJECTED", JobExecutionResult.Blocked("PRODUCT_UPDATE_CONTENT_REJECTED", "Trendyol content güncellemesi reddedildi.", payload.ExternalOperationId), cancellationToken);
        if (phase is "POLL_UNAPPROVED" or "POLL_VARIANTS" or "POLL_DELIVERY")
        {
            if (status.Lines.Count == 0) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "PRODUCT_UPDATE_BATCH_CONTRACT_INVALID", JobExecutionResult.ManualReview("PRODUCT_UPDATE_BATCH_CONTRACT_INVALID", "Ürün güncelleme batch sonucu satır içermiyor.", payload.ExternalOperationId), cancellationToken);
            var byBarcode = status.Lines.Where(x => !string.IsNullOrWhiteSpace(x.ExternalKey)).GroupBy(x => x.ExternalKey, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);
            foreach (var listing in listings)
            {
                if (string.IsNullOrWhiteSpace(listing.ExternalBarcode) || !byBarcode.TryGetValue(listing.ExternalBarcode, out var line)) continue;
                if (line.Succeeded) continue;
                var code = SafeCode(line.ErrorCode) ?? "PRODUCT_UPDATE_REJECTED";
                listing.ActualStatus = "UPDATE_REJECTED"; listing.RejectionCode = code;
                if (states.TryGetValue(listing.VariantId, out var state)) { state.ActualStatus = "UPDATE_REJECTED"; state.LastRejectionCode = code; state.Version++; }
            }
        }

        var next = AdvanceUpdate(payload, phase);
        if (next is null) return await CompleteProductUpdate(tenantId, connectionId, payload, profile, listings, states, correlationId, cancellationToken);
        job.PayloadJson = JsonSerializer.Serialize(next); job.PayloadHash = Hash(job.PayloadJson);
        profile.ActualStatus = "UPDATE_IN_PROGRESS"; profile.Version++;
        await db.SaveChangesAsync(cancellationToken);
        return JobExecutionResult.Retry("PRODUCT_UPDATE_NEXT_PHASE", "Trendyol ürün güncellemesinin sonraki batch fazı hazırlanıyor.", TimeSpan.FromSeconds(1));
    }

    private async Task<JobExecutionResult> CompleteProductUpdate(Guid tenantId, Guid connectionId, ProductUpdateJobPayload payload, ChannelListingProfile profile, IReadOnlyList<ChannelListingVariant> listings, IReadOnlyDictionary<Guid, MarketplaceListingState> states, string correlationId, CancellationToken cancellationToken)
    {
        var rejected = listings.Count(x => string.Equals(x.ActualStatus, "UPDATE_REJECTED", StringComparison.Ordinal));
        foreach (var listing in listings.Where(x => !string.Equals(x.ActualStatus, "UPDATE_REJECTED", StringComparison.Ordinal)))
        {
            listing.ActualStatus = "APPROVAL_PENDING"; listing.RejectionCode = null;
            if (states.TryGetValue(listing.VariantId, out var state)) { state.ActualStatus = "APPROVAL_PENDING"; state.LastRejectionCode = null; state.Version++; }
        }
        profile.ActualStatus = rejected == 0 ? "APPROVAL_PENDING" : rejected == listings.Count ? "UPDATE_REJECTED" : "UPDATE_PARTIAL_FAILURE";
        profile.LastRejectionCode = listings.Select(x => x.RejectionCode).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)); profile.Version++;
        if (rejected < listings.Count) await EnsureApprovalReconciliationJob(tenantId, connectionId, payload.ProductId, profile.Id, payload.PayloadHash, correlationId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return rejected == 0 ? JobExecutionResult.Success() : rejected == listings.Count ? JobExecutionResult.Blocked("PRODUCT_UPDATE_REJECTED", "Trendyol ürün güncellemesindeki tüm varyantlar reddedildi.") : JobExecutionResult.Blocked("PRODUCT_UPDATE_PARTIAL_FAILURE", "Trendyol ürün güncellemesi bazı varyantlar için reddedildi.");
    }

    private async Task<JobExecutionResult> ArchiveProduct(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        ProductArchiveJobPayload? payload;
        try { payload = JsonSerializer.Deserialize<ProductArchiveJobPayload>(payloadJson, JsonOptions); }
        catch (JsonException) { return JobExecutionResult.Blocked("PRODUCT_ARCHIVE_PAYLOAD_INVALID", "Ürün arşiv işi payload sözleşmesini sağlamıyor."); }
        if (payload is null || payload.JobId == Guid.Empty || payload.ProductId == Guid.Empty || payload.ProfileId == Guid.Empty || string.IsNullOrWhiteSpace(payload.PayloadHash) || payload.DeadlineAt <= payload.StartedAt)
            return JobExecutionResult.Blocked("PRODUCT_ARCHIVE_PAYLOAD_INVALID", "Ürün arşiv işi zorunlu alanları eksik.");
        var job = await db.IntegrationJobs.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == payload.JobId && x.ConnectionId == connectionId && x.JobType == F3JobTypes.ProductArchive, cancellationToken);
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
            var effectKey = job.EffectIdempotencyKey;
            if (await db.ExternalEffectRecords.AnyAsync(x => x.TenantId == tenantId && x.EffectType == F3JobTypes.ProductArchive && x.IdempotencyKey == effectKey, cancellationToken)) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "EXTERNAL_EFFECT_AMBIGUOUS", JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS", "Önceki arşiv çağrısının sonucu kesinleştirilemedi."), cancellationToken);
            var effect = new ExternalEffectRecord { Id = Guid.CreateVersion7(), TenantId = tenantId, EffectType = F3JobTypes.ProductArchive, IdempotencyKey = effectKey, CreatedAt = timeProvider.GetUtcNow() };
            db.ExternalEffectRecords.Add(effect); await db.SaveChangesAsync(cancellationToken);
            var submit = await products.ArchiveAsync(Context(tenantId, connectionId, correlationId, effectKey), payload.PayloadJson, cancellationToken);
            if (!submit.IsSuccess)
            {
                if (IsAmbiguous(submit.Error!)) return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "EXTERNAL_EFFECT_AMBIGUOUS", JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS", "Arşiv çağrısının uzak tarafta uygulanıp uygulanmadığı kesinleştirilemedi.", submit.Error!.RemoteRequestId), cancellationToken);
                db.ExternalEffectRecords.Remove(effect); await db.SaveChangesAsync(cancellationToken);
                return await MarkPublicationResult(tenantId, connectionId, profile, "ARCHIVE_BLOCKED", submit.Error!.Code, JobExecutionResult.FromAdapterError(submit.Error), cancellationToken);
            }
            effect.CompletedAt = timeProvider.GetUtcNow(); var op = submit.Value!;
            var next = payload with { Phase = "POLL", ExternalOperationId = op.ExternalOperationId };
            job.PayloadJson = JsonSerializer.Serialize(next); job.PayloadHash = Hash(job.PayloadJson); profile.ActualStatus = "ARCHIVE_BATCH_SUBMITTED"; profile.Version++;
            await db.SaveChangesAsync(cancellationToken);
            return JobExecutionResult.Retry("PRODUCT_ARCHIVE_BATCH_PENDING", "Trendyol arşiv batch sonucu bekleniyor.", TimeSpan.FromSeconds(15), op.ExternalOperationId);
        }
        if (phase == "POLL")
        {
            if (string.IsNullOrWhiteSpace(payload.ExternalOperationId)) return JobExecutionResult.Blocked("PRODUCT_ARCHIVE_PHASE_INVALID", "Arşiv poll fazında batch kimliği eksik.");
            var result = await products.GetOperationAsync(Context(tenantId, connectionId, correlationId, $"{job.EffectIdempotencyKey}:poll"), payload.ExternalOperationId, cancellationToken);
            if (!result.IsSuccess) return JobExecutionResult.FromAdapterError(result.Error!);
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
            var result = await products.GetPublicationStatusAsync(Context(tenantId, connectionId, correlationId, $"archive-readback:{profile.Id:N}:{listing.ExternalBarcode}"), listing.ExternalBarcode, cancellationToken);
            if (!result.IsSuccess) { var mapped = JobExecutionResult.FromAdapterError(result.Error!); if (mapped.Kind == JobCompletionKind.Retry) return mapped; return await MarkPublicationResult(tenantId, connectionId, profile, "MANUAL_REVIEW", result.Error!.Code, JobExecutionResult.ManualReview(result.Error.Code, result.Error.SafeMessage, result.Error.RemoteRequestId), cancellationToken); }
            var desiredReached = payload.Archived ? result.Value!.Status == "ARCHIVED" : result.Value!.Status == "APPROVED";
            if (desiredReached)
            {
                var status = payload.Archived ? "ARCHIVED" : "LIVE"; listing.ActualStatus = status; listing.RejectionCode = null;
                if (states.TryGetValue(listing.VariantId, out var state)) { state.ActualStatus = status; state.LastRejectionCode = null; state.Version++; }
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

    private async Task<JobExecutionResult> SyncPriceInventory(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        PriceInventoryJobPayload? payload;
        try { payload = JsonSerializer.Deserialize<PriceInventoryJobPayload>(payloadJson, JsonOptions); }
        catch (JsonException) { return JobExecutionResult.Blocked("PRICE_INVENTORY_PAYLOAD_INVALID", "Fiyat-stok işi payload sözleşmesini sağlamıyor."); }
        if (payload is null || payload.JobId == Guid.Empty || payload.ConnectionId != connectionId || string.IsNullOrWhiteSpace(payload.PayloadHash) || string.IsNullOrWhiteSpace(payload.PayloadJson)) return JobExecutionResult.Blocked("PRICE_INVENTORY_PAYLOAD_INVALID", "Fiyat-stok işi zorunlu alanları eksik.");
        var job = await db.IntegrationJobs.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == payload.JobId && x.ConnectionId == connectionId && x.JobType == F3JobTypes.PriceInventorySync, cancellationToken);
        if (job is null) return JobExecutionResult.Blocked("PRICE_INVENTORY_STATE_MISSING", "Fiyat-stok işi bulunamadı.");
        var phase = payload.Phase.Trim().ToUpperInvariant();
        if (phase == "SUBMIT")
        {
            var current = await new PriceInventoryComposer(db).BuildAsync(tenantId, connectionId, cancellationToken);
            if (!current.Succeeded)
            {
                if (current.Error!.Code == "NO_EXTERNAL_CHANGES") return JobExecutionResult.Success();
                return JobExecutionResult.Blocked(current.Error.Code, current.Error.Message);
            }
            if (!string.Equals(current.Value!.PayloadHash, payload.PayloadHash, StringComparison.Ordinal)) return JobExecutionResult.Blocked("PRICE_INVENTORY_SUPERSEDED", "Fiyat veya stok yeni bir sürüme geçti; eski payload uzak sisteme gönderilmedi.");
            if (await db.ExternalEffectRecords.AnyAsync(x => x.TenantId == tenantId && x.EffectType == F3JobTypes.PriceInventorySync && x.IdempotencyKey == job.EffectIdempotencyKey, cancellationToken)) return JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS", "Önceki fiyat-stok çağrısının sonucu kesinleştirilemedi; tekrar gönderim engellendi.");
            var effect = new ExternalEffectRecord { Id = Guid.CreateVersion7(), TenantId = tenantId, EffectType = F3JobTypes.PriceInventorySync, IdempotencyKey = job.EffectIdempotencyKey, CreatedAt = timeProvider.GetUtcNow() };
            db.ExternalEffectRecords.Add(effect); await db.SaveChangesAsync(cancellationToken);
            var submit = await inventoryPrice.PushPriceAndInventoryAsync(Context(tenantId, connectionId, correlationId, job.EffectIdempotencyKey), payload.PayloadJson, cancellationToken);
            if (!submit.IsSuccess)
            {
                if (IsAmbiguous(submit.Error!)) return JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS", "Fiyat-stok çağrısının uygulanıp uygulanmadığı kesinleştirilemedi.", submit.Error!.RemoteRequestId);
                db.ExternalEffectRecords.Remove(effect); await db.SaveChangesAsync(cancellationToken); return JobExecutionResult.FromAdapterError(submit.Error!);
            }
            effect.CompletedAt = timeProvider.GetUtcNow(); var op = submit.Value!;
            var next = payload with { Phase = "POLL", ExternalOperationId = op.ExternalOperationId, SubmittedAt = op.SubmittedAt };
            job.PayloadJson = JsonSerializer.Serialize(next); job.PayloadHash = Hash(job.PayloadJson); await db.SaveChangesAsync(cancellationToken);
            return JobExecutionResult.Retry("PRICE_INVENTORY_BATCH_PENDING", "Trendyol fiyat-stok batch sonucu bekleniyor.", TimeSpan.FromSeconds(15), op.ExternalOperationId);
        }
        if (phase != "POLL" || string.IsNullOrWhiteSpace(payload.ExternalOperationId) || payload.SubmittedAt is null) return JobExecutionResult.Blocked("PRICE_INVENTORY_PHASE_INVALID", "Fiyat-stok işi bilinmeyen bir fazda.");
        if (timeProvider.GetUtcNow() - payload.SubmittedAt.Value > TimeSpan.FromHours(4)) return JobExecutionResult.ManualReview("PRICE_INVENTORY_BATCH_EXPIRED", "Fiyat-stok batch sonucu dört saatlik pencerede alınamadı.", payload.ExternalOperationId);
        var operation = await products.GetOperationAsync(Context(tenantId, connectionId, correlationId, $"{job.EffectIdempotencyKey}:poll"), payload.ExternalOperationId, cancellationToken);
        if (!operation.IsSuccess) return JobExecutionResult.FromAdapterError(operation.Error!);
        if (operation.Value!.Status.Equals("IN_PROGRESS", StringComparison.OrdinalIgnoreCase)) return JobExecutionResult.Retry("PRICE_INVENTORY_BATCH_PENDING", "Trendyol fiyat-stok batch sonucu bekleniyor.", TimeSpan.FromSeconds(20), payload.ExternalOperationId);
        if (!operation.Value.Status.Equals("COMPLETED", StringComparison.OrdinalIgnoreCase)) return JobExecutionResult.ManualReview("PRICE_INVENTORY_BATCH_STATUS_UNKNOWN", "Fiyat-stok batch servisi tanınmayan durum döndürdü.", payload.ExternalOperationId);
        if (payload.Lines.Count == 0 || payload.Lines.Count > 1000 || payload.Lines.Select(x => x.Barcode).Distinct(StringComparer.OrdinalIgnoreCase).Count() != payload.Lines.Count)
            return JobExecutionResult.ManualReview("PRICE_INVENTORY_PAYLOAD_INVALID", "Fiyat-stok job satırları eksik, yinelenen veya limit dışı.");
        var offerIds = payload.Lines.Select(x => x.OfferId).ToArray();
        var offers = await db.ChannelOffers.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && offerIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        if (offers.Count != payload.Lines.Count) return JobExecutionResult.ManualReview("PRICE_INVENTORY_STATE_INCOMPLETE", "Fiyat-stok uzlaştırmasında teklif kayıtları eksik.");
        var variantIds = payload.Lines.Select(x => x.VariantId).Distinct().ToArray();
        var inventory = await db.InventoryItems.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.LocationCode == "MAIN" && variantIds.Contains(x.VariantId))
            .ToDictionaryAsync(x => x.VariantId, cancellationToken);
        if (inventory.Count != variantIds.Length) return JobExecutionResult.ManualReview("PRICE_INVENTORY_STATE_INCOMPLETE", "Fiyat-stok uzlaştırmasında MAIN stok projection kayıtları eksik.");
        foreach (var line in payload.Lines)
        {
            if (offers[line.OfferId].PriceVersion != line.PriceVersion || inventory[line.VariantId].ProjectionVersion != line.ProjectionVersion)
                return JobExecutionResult.Blocked("PRICE_INVENTORY_SUPERSEDED", "Batch sonucu alınırken fiyat veya stok daha yeni bir sürüme geçti; eski sonuç güncel kayda uygulanmadı.");
        }
        var lineByBarcode = operation.Value.Lines.Where(x => !string.IsNullOrWhiteSpace(x.ExternalKey)).GroupBy(x => x.ExternalKey, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);
        var success = 0; var failed = 0;
        foreach (var line in payload.Lines)
        {
            if (!lineByBarcode.TryGetValue(line.Barcode, out var remoteLine)) return JobExecutionResult.ManualReview("PRICE_INVENTORY_BATCH_CONTRACT_INVALID", "Fiyat-stok batch sonucu tüm barkodları içermiyor.", payload.ExternalOperationId);
            var offer = offers[line.OfferId];
            if (remoteLine.Succeeded) { offer.LastPriceHash = line.PriceHash; offer.LastStockProjectionVersion = line.ProjectionVersion; offer.Version++; success++; }
            else { failed++; await RecordIssue(tenantId, $"price-inventory:{connectionId}:{line.Barcode}:{SafeCode(remoteLine.ErrorCode)}", SafeCode(remoteLine.ErrorCode) ?? "PRICE_INVENTORY_LINE_REJECTED", $"Trendyol fiyat-stok satırı reddedildi: {line.Barcode}.", cancellationToken); }
        }
        await db.SaveChangesAsync(cancellationToken);
        if (failed == 0) return JobExecutionResult.Success();
        return success == 0 ? JobExecutionResult.Blocked("PRICE_INVENTORY_REJECTED", "Trendyol fiyat-stok batch içindeki tüm satırları reddetti.", payload.ExternalOperationId) : JobExecutionResult.ManualReview("PRICE_INVENTORY_PARTIAL_FAILURE", "Trendyol fiyat-stok batch kısmi başarısızlıkla tamamlandı.", payload.ExternalOperationId);
    }

    private async Task<JobExecutionResult> CommonLabel(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        CommonLabelJobPayload? payload;
        try { payload = JsonSerializer.Deserialize<CommonLabelJobPayload>(payloadJson, JsonOptions); }
        catch (JsonException) { return JobExecutionResult.Blocked("COMMON_LABEL_PAYLOAD_INVALID", "Ortak etiket işi payload sözleşmesini sağlamıyor."); }
        if (payload is null || payload.JobId == Guid.Empty || payload.PackageId == Guid.Empty || payload.BoxQuantity < 1 || payload.VolumetricHeight < 0 || payload.DeadlineAt <= payload.StartedAt) return JobExecutionResult.Blocked("COMMON_LABEL_PAYLOAD_INVALID", "Ortak etiket işi zorunlu alanları eksik.");
        var job = await db.IntegrationJobs.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == payload.JobId && x.ConnectionId == connectionId && x.JobType == F3JobTypes.CommonLabel, cancellationToken);
        var package = await db.ShipmentPackages.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == payload.PackageId && x.ConnectionId == connectionId, cancellationToken);
        var attempt = await db.ShipmentDocumentAttempts.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.IdempotencyKey == (job == null ? "" : job.EffectIdempotencyKey), cancellationToken);
        if (job is null || package is null || attempt is null) return JobExecutionResult.Blocked("COMMON_LABEL_STATE_MISSING", "Ortak etiket işi, paket veya deneme kaydı bulunamadı.");
        if (string.IsNullOrWhiteSpace(package.CargoTrackingNumber)) return JobExecutionResult.Blocked("CARGO_TRACKING_REQUIRED", "Ortak etiket için kargo takip numarası gerekir.");
        if (timeProvider.GetUtcNow() > payload.DeadlineAt) { attempt.Status = "MANUAL_REVIEW"; attempt.ErrorCode = "COMMON_LABEL_DEADLINE_EXPIRED"; attempt.CompletedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); return JobExecutionResult.ManualReview("COMMON_LABEL_DEADLINE_EXPIRED", "Ortak etiket belirlenen pencerede hazır olmadı."); }
        var phase = payload.Phase.Trim().ToUpperInvariant();
        if (phase == "SUBMIT")
        {
            if (await db.ExternalEffectRecords.AnyAsync(x => x.TenantId == tenantId && x.EffectType == F3JobTypes.CommonLabel && x.IdempotencyKey == job.EffectIdempotencyKey, cancellationToken)) return JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS", "Önceki ortak etiket oluşturma çağrısının sonucu kesinleştirilemedi.");
            var effect = new ExternalEffectRecord { Id = Guid.CreateVersion7(), TenantId = tenantId, EffectType = F3JobTypes.CommonLabel, IdempotencyKey = job.EffectIdempotencyKey, CreatedAt = timeProvider.GetUtcNow() };
            db.ExternalEffectRecords.Add(effect); await db.SaveChangesAsync(cancellationToken);
            var create = await orders.CreateCommonLabelAsync(Context(tenantId, connectionId, correlationId, job.EffectIdempotencyKey), new(package.CargoTrackingNumber, payload.BoxQuantity, payload.VolumetricHeight), cancellationToken);
            if (!create.IsSuccess)
            {
                if (IsAmbiguous(create.Error!)) { attempt.Status = "MANUAL_REVIEW"; attempt.ErrorCode = "EXTERNAL_EFFECT_AMBIGUOUS"; attempt.CompletedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); return JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS", "Ortak etiket oluşturma çağrısının sonucu kesinleştirilemedi.", create.Error!.RemoteRequestId); }
                db.ExternalEffectRecords.Remove(effect); attempt.Status = "FAILED"; attempt.ErrorCode = create.Error!.Code; attempt.CompletedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); return JobExecutionResult.FromAdapterError(create.Error);
            }
            effect.CompletedAt = timeProvider.GetUtcNow(); attempt.Status = "POLLING";
            var next = payload with { Phase = "POLL" }; job.PayloadJson = JsonSerializer.Serialize(next); job.PayloadHash = Hash(job.PayloadJson); await db.SaveChangesAsync(cancellationToken);
            return JobExecutionResult.Retry("COMMON_LABEL_PENDING", "Trendyol ortak etiket hazırlanıyor.", TimeSpan.FromSeconds(10));
        }
        if (phase != "POLL") return JobExecutionResult.Blocked("COMMON_LABEL_PHASE_INVALID", "Ortak etiket işi bilinmeyen bir fazda.");
        var documentResult = await orders.GetCommonLabelAsync(Context(tenantId, connectionId, correlationId, $"{job.EffectIdempotencyKey}:poll"), package.CargoTrackingNumber, cancellationToken);
        if (!documentResult.IsSuccess)
        {
            var error = documentResult.Error!;
            var mapped = JobExecutionResult.FromAdapterError(error);
            if (mapped.Kind == JobCompletionKind.Retry || error.Class == AdapterErrorClass.NotFound) return JobExecutionResult.Retry("COMMON_LABEL_PENDING", "Trendyol ortak etiket henüz hazır değil.", TimeSpan.FromSeconds(20), error.RemoteRequestId);
            attempt.Status = mapped.Kind == JobCompletionKind.ManualReview ? "MANUAL_REVIEW" : "FAILED"; attempt.ErrorCode = error.Code; attempt.CompletedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); return mapped;
        }
        var document = documentResult.Value!;
        if (!string.Equals(document.Format, "ZPL", StringComparison.OrdinalIgnoreCase) || document.Content.Length == 0 || document.Content.LongLength > 5 * 1024 * 1024)
        {
            attempt.Status = "MANUAL_REVIEW"; attempt.ErrorCode = "COMMON_LABEL_CONTRACT_INVALID"; attempt.CompletedAt = timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
            return JobExecutionResult.ManualReview("COMMON_LABEL_CONTRACT_INVALID", "Ortak etiket yalnız ZPL ve 5 MiB altı içerik olarak kabul edilir.");
        }
        var checksum = Convert.ToHexString(SHA256.HashData(document.Content));
        var existing = await db.ShipmentDocuments.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.PackageId == package.Id && x.DocumentKind == "COMMON_LABEL" && x.Checksum == checksum, cancellationToken);
        if (existing is null)
        {
            var assetId = Guid.CreateVersion7(); await using var stream = new MemoryStream(document.Content, writable: false); var stored = await files.SaveAsync(tenantId, $"{assetId:N}-common-label.zpl", "application/zpl", stream, 5 * 1024 * 1024, cancellationToken);
            var asset = new FileAsset { Id = assetId, TenantId = tenantId, Classification = "SHIPMENT_LABEL", RelativePath = stored, OriginalNameSafe = $"{package.CargoTrackingNumber}.zpl", MimeType = "application/zpl", SizeBytes = document.Content.LongLength, Sha256 = checksum, Status = "ACTIVE", CreatedAt = timeProvider.GetUtcNow() };
            db.FileAssets.Add(asset);
            var version = await db.ShipmentDocuments.Where(x => x.TenantId == tenantId && x.PackageId == package.Id && x.DocumentKind == "COMMON_LABEL").Select(x => (int?)x.DocumentVersion).MaxAsync(cancellationToken) ?? 0;
            existing = new ShipmentDocument { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, PackageId = package.Id, FileAssetId = assetId, DocumentKind = "COMMON_LABEL", Format = "ZPL", Source = "TRENDYOL", Checksum = checksum, DocumentVersion = version + 1, CreatedAt = timeProvider.GetUtcNow() };
            db.ShipmentDocuments.Add(existing);
        }
        attempt.DocumentId = existing.Id; attempt.Status = "SUCCEEDED"; attempt.ErrorCode = null; attempt.CompletedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); return JobExecutionResult.Success();
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static bool IsAmbiguous(AdapterError error) => error.Class is AdapterErrorClass.TransientNetwork or AdapterErrorClass.Remote5xx or AdapterErrorClass.ContractViolation or AdapterErrorClass.InternalBug;
    private static bool HasItems(string payloadJson) { try { using var doc = JsonDocument.Parse(payloadJson); return doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0; } catch (JsonException) { return false; } }
    private static string UpdatePayload(ProductUpdateJobPayload payload, string phase) => phase switch { "SUBMIT_UNAPPROVED" or "POLL_UNAPPROVED" => payload.UnapprovedPayloadJson, "SUBMIT_CONTENT" or "POLL_CONTENT" => payload.ApprovedContentPayloadJson, "SUBMIT_VARIANTS" or "POLL_VARIANTS" => payload.ApprovedVariantPayloadJson, "SUBMIT_DELIVERY" or "POLL_DELIVERY" => payload.ApprovedDeliveryPayloadJson, _ => "{}" };
    private static ProductUpdateJobPayload? AdvanceUpdate(ProductUpdateJobPayload payload, string phase)
    {
        var next = phase switch
        {
            "SUBMIT_UNAPPROVED" or "POLL_UNAPPROVED" => null,
            "SUBMIT_CONTENT" or "POLL_CONTENT" => "SUBMIT_VARIANTS",
            "SUBMIT_VARIANTS" or "POLL_VARIANTS" => "SUBMIT_DELIVERY",
            "SUBMIT_DELIVERY" or "POLL_DELIVERY" => null,
            _ => null
        };
        return next is null ? null : payload with { Phase = next, ExternalOperationId = null, SubmittedAt = null };
    }


    private async Task<bool> SyncReferences(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        var (resourceType, parentExternalId) = ReferenceResource(payloadJson);
        if (!await IsValidReferenceScope(tenantId, connectionId, resourceType, parentExternalId, cancellationToken)) return false;
        var items = new List<RemoteReferenceItem>();
        var visitedCursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var result = await references.ReadAsync(Context(tenantId, connectionId, correlationId, $"reference-sync:{resourceType}:{parentExternalId}:{cursor}"), new(resourceType, parentExternalId), new(cursor, 1000), cancellationToken);
            if (!result.IsSuccess) throw JobProcessingException.FromAdapter(result.Error!);
            items.AddRange(result.Value!.Items);
            if (items.Count > 100_000) return false;
            cursor = result.Value.NextCursor;
            if (!result.Value.HasMore) break;
            if (string.IsNullOrWhiteSpace(cursor) || !visitedCursors.Add(cursor)) return false;
        } while (!cancellationToken.IsCancellationRequested);

        cancellationToken.ThrowIfCancellationRequested();
        if ((items.Count == 0 && resourceType is "CATEGORIES" or "BRANDS") || items.Any(x => !string.Equals(x.ResourceType, resourceType, StringComparison.Ordinal) || !string.Equals(x.ParentExternalId ?? "", parentExternalId ?? "", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(x.ExternalId) || string.IsNullOrWhiteSpace(x.Name))) return false;
        var ordered = items.OrderBy(x => x.ExternalId, StringComparer.Ordinal).ToList();
        if (ordered.Select(x => x.ExternalId).Distinct(StringComparer.Ordinal).Count() != ordered.Count) return false;
        var canonical = JsonSerializer.Serialize(ordered.Select(x => new { x.ExternalId, x.ParentExternalId, x.Name, x.Path, x.Depth, x.IsLeaf, x.IsActive, x.IsRequired, x.AllowsCustomValue, x.AllowsMultipleValues }));
        var contentHash = Hash(canonical);
        var now = timeProvider.GetUtcNow();
        var sourceVersion = await db.PlatformCapabilities.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Code == F3Capabilities.ReferenceRead).Select(x => x.SourceVersion).SingleOrDefaultAsync(cancellationToken)
            ?? await db.PlatformConnections.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == connectionId).Select(x => x.ApiVersion).SingleAsync(cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var scope = parentExternalId ?? "";
        var snapshots = await db.ReferenceSnapshots.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == resourceType && x.ScopeExternalId == scope).ToListAsync(cancellationToken);
        var snapshot = snapshots.SingleOrDefault(x => x.ContentHash == contentHash);
        foreach (var current in snapshots.Where(x => x.IsCurrent && x.Id != snapshot?.Id)) current.IsCurrent = false;
        if (snapshot is null)
        {
            snapshot = new ReferenceSnapshot { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, ResourceType = resourceType, ScopeExternalId = scope, SourceVersion = sourceVersion, ContentHash = contentHash, FetchedAt = now, IsCurrent = true, ItemCount = ordered.Count };
            db.ReferenceSnapshots.Add(snapshot);
            for (var index = 0; index < ordered.Count; index++)
            {
                var item = ordered[index];
                db.ReferenceItems.Add(new ReferenceItem { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, SnapshotId = snapshot.Id, ResourceType = resourceType, ExternalId = item.ExternalId, ParentExternalId = item.ParentExternalId, Name = item.Name, NormalizedName = item.Name.Trim().ToUpperInvariant(), Path = item.Path, Depth = item.Depth, IsLeaf = item.IsLeaf, IsActive = item.IsActive, IsRequired = item.IsRequired, AllowsCustomValue = item.AllowsCustomValue, AllowsMultipleValues = item.AllowsMultipleValues, PayloadHash = Hash(item.RawJson), SortOrder = index });
            }
        }
        else
        {
            snapshot.IsCurrent = true;
            snapshot.FetchedAt = now;
            snapshot.SourceVersion = sourceVersion;
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static (string ResourceType, string? ParentExternalId) ReferenceResource(string payloadJson)
    {
        try
        {
            using var payload = JsonDocument.Parse(payloadJson);
            var resourceType = payload.RootElement.TryGetProperty("resourceType", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim().ToUpperInvariant() : "CATEGORIES";
            var parent = payload.RootElement.TryGetProperty("parentExternalId", out var parentValue) && parentValue.ValueKind == JsonValueKind.String ? parentValue.GetString()?.Trim() : null;
            return (resourceType, string.IsNullOrWhiteSpace(parent) ? null : parent);
        }
        catch (JsonException) { return ("", null); }
    }

    private async Task<bool> IsValidReferenceScope(Guid tenantId, Guid connectionId, string resourceType, string? parentExternalId, CancellationToken cancellationToken)
    {
        if (resourceType is "CATEGORIES" or "BRANDS") return parentExternalId is null;
        if (resourceType == "CATEGORY_ATTRIBUTES" && parentExternalId is not null)
            return await db.ReferenceItems.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "CATEGORIES" && x.ExternalId == parentExternalId && x.IsLeaf && x.IsActive
                && db.ReferenceSnapshots.Any(snapshot => snapshot.TenantId == tenantId && snapshot.Id == x.SnapshotId && snapshot.IsCurrent && snapshot.ScopeExternalId == ""), cancellationToken);
        if (resourceType != "ATTRIBUTE_VALUES" || parentExternalId is null) return false;
        var parts = parentExternalId.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2) return false;
        return await db.ReferenceItems.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "CATEGORY_ATTRIBUTES" && x.ExternalId == parts[1] && x.ParentExternalId == parts[0]
            && db.ReferenceSnapshots.Any(snapshot => snapshot.TenantId == tenantId && snapshot.Id == x.SnapshotId && snapshot.IsCurrent && snapshot.ScopeExternalId == parts[0]), cancellationToken);
    }

    private async Task<bool> TestConnection(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        var connection = await db.PlatformConnections.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && x.PlatformCode == "TRENDYOL", cancellationToken); if (connection is null) return false; var now = timeProvider.GetUtcNow(); connection.LastTestedAt = now;
        IConnectionPort port = connections;
        var context = Context(tenantId, connectionId, correlationId, "connection-test"); var result = await port.TestAsync(context, cancellationToken); if (!result.IsSuccess) { connection.LastErrorCode = result.Error!.Code; connection.Version++; await db.SaveChangesAsync(cancellationToken); throw JobProcessingException.FromAdapter(result.Error!); }
        var discovery = await port.DiscoverCapabilitiesAsync(context, cancellationToken); if (!discovery.IsSuccess) { connection.LastErrorCode = discovery.Error!.Code; connection.Version++; await db.SaveChangesAsync(cancellationToken); throw JobProcessingException.FromAdapter(discovery.Error!); }
        foreach (var evidence in discovery.Value!)
        {
            var capability = await db.PlatformCapabilities.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.Code == evidence.Code, cancellationToken); if (capability is null) continue;
            capability.SupportLevel = string.Equals(evidence.SupportLevel, "SUPPORTED", StringComparison.Ordinal) ? CapabilitySupportLevel.Supported : CapabilitySupportLevel.Unknown; capability.SourceUrl = evidence.SourceUrl; capability.SourceVersion = evidence.SourceVersion; capability.RequiredScope = evidence.RequiredScope; capability.ConstraintsJson = evidence.ConstraintsJson; capability.EvidenceNote = evidence.EvidenceNote; capability.FixtureChecksum = evidence.FixtureChecksum; capability.VerifiedAt = evidence.VerifiedAt; capability.Version++;
        }
        connection.LastSuccessAt = now; connection.LastErrorCode = null; if (connection.Status == "DRAFT") connection.Status = "VERIFIED"; connection.Version++; await db.SaveChangesAsync(cancellationToken); return true;
    }


    private async Task<bool> SyncOrders(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        string? externalOrderId = null;
        try { using var payload = JsonDocument.Parse(payloadJson); if (payload.RootElement.TryGetProperty("externalOrderId", out var value) && value.ValueKind == JsonValueKind.String) externalOrderId = value.GetString(); }
        catch (JsonException) { return false; }
        if (!string.IsNullOrWhiteSpace(externalOrderId))
        {
            var single = await orders.GetAsync(Context(tenantId, connectionId, correlationId, $"order-get:{externalOrderId}"), externalOrderId.Trim(), cancellationToken);
            if (!single.IsSuccess) throw JobProcessingException.FromAdapter(single.Error!);
            await UpsertOrder(tenantId, connectionId, single.Value!, cancellationToken);
            return true;
        }

        var cursor = await Cursor(tenantId, connectionId, "ORDERS", cancellationToken); var policy = await db.ConnectionSyncPolicies.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "ORDERS", cancellationToken); var modifiedAfter = cursor.LastModifiedWatermark?.Subtract(TimeSpan.FromSeconds(policy?.OverlapSeconds ?? 0)); var next = cursor.OpaqueCursor;
        do
        {
            var result = await orders.PollAsync(Context(tenantId, connectionId, correlationId, $"order-sync:{next}"), new(modifiedAfter, null), new(next, 200), cancellationToken); if (!result.IsSuccess) throw JobProcessingException.FromAdapter(result.Error!);
            foreach (var order in result.Value!.Items) await UpsertOrder(tenantId, connectionId, order, cancellationToken);
            next = result.Value.NextCursor; cursor.OpaqueCursor = next; cursor.LastModifiedWatermark = result.Value.Items.Select(x => (DateTimeOffset?)x.LastModifiedAt).Max() ?? cursor.LastModifiedWatermark; cursor.LastSuccessAt = timeProvider.GetUtcNow(); cursor.Version++; await db.SaveChangesAsync(cancellationToken); if (!result.Value.HasMore) break;
        } while (!cancellationToken.IsCancellationRequested);
        return true;
    }

    private async Task<bool> IngestWebhook(Guid tenantId, Guid connectionId, string payloadJson, CancellationToken cancellationToken)
    {
        string raw; string externalMessageId; try { using var payload = JsonDocument.Parse(payloadJson); raw = payload.RootElement.GetProperty("rawJson").GetString() ?? ""; externalMessageId = payload.RootElement.GetProperty("externalMessageId").GetString() ?? ""; } catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException) { return false; }
        AdapterPageResult<RemoteOrder> page; try { page = TrendyolJsonMapper.Orders(raw); } catch (JsonException) { return false; }
        foreach (var order in page.Items) await UpsertOrder(tenantId, connectionId, order, cancellationToken); var inbox = await db.InboxMessages.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Source == "TRENDYOL_WEBHOOK" && x.ExternalMessageId == externalMessageId, cancellationToken); if (inbox is not null) inbox.ProcessedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); return true;
    }

    private async Task UpsertOrder(Guid tenantId, Guid connectionId, RemoteOrder remote, CancellationToken cancellationToken)
    {
        if (!PackageIngestionSafety.TryGetOrderedQuantities(remote.Lines, out var remoteLineQuantities)) { await RecordIssue(tenantId, $"order-lines:{connectionId}:{remote.ExternalOrderId}:{remote.LastModifiedAt.ToUnixTimeMilliseconds()}", "ORDER_LINE_QUANTITY_INVARIANT_REJECTED", "Sipariş satır kimliği veya miktarı geçersizdi; olayın hiçbir parçası uygulanmadı.", cancellationToken); await db.SaveChangesAsync(cancellationToken); return; }
        foreach (var remotePackage in remote.Packages) if (!PackageIngestionSafety.TryNormalizeAll(remoteLineQuantities, remotePackage.Allocations, CanonicalPackage(remotePackage.RawStatus), out _)) { var rejectedEventId = PackageIngestionSafety.EventId(remotePackage.ExternalPackageId, remotePackage.OccurredAt); await RecordIssue(tenantId, $"package-quantity:{connectionId}:{rejectedEventId}", "PACKAGE_QUANTITY_INVARIANT_REJECTED", "Package miktarları sipariş satırı bütünlüğünü sağlamadı; olayın hiçbir parçası uygulanmadı.", cancellationToken); await db.SaveChangesAsync(cancellationToken); return; }
        var now = timeProvider.GetUtcNow(); var order = await db.Orders.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalOrderId == remote.ExternalOrderId, cancellationToken);
        if (order is not null && remote.Packages.Count > 0) { var candidateEventIds = remote.Packages.Select(x => PackageIngestionSafety.EventId(x.ExternalPackageId, x.OccurredAt)).ToHashSet(StringComparer.Ordinal); var recordedEventIds = (await db.OrderStatusHistory.AsNoTracking().Where(x => x.TenantId == tenantId && x.OrderId == order.Id && candidateEventIds.Contains(x.SourceEventId)).Select(x => x.SourceEventId).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal); foreach (var localEvent in db.OrderStatusHistory.Local.Where(x => x.TenantId == tenantId && x.OrderId == order.Id && candidateEventIds.Contains(x.SourceEventId))) recordedEventIds.Add(localEvent.SourceEventId); if (remote.Lines.Count == 0 && PackageIngestionSafety.AllEventsRecorded(remote.Packages, recordedEventIds)) return; }
        if (order is null) { order = new Order { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, ExternalOrderId = remote.ExternalOrderId, OrderNumber = remote.OrderNumber, Currency = remote.Currency, CustomerSnapshotJson = remote.CustomerSnapshotJson, ShipmentAddressSnapshotJson = remote.ShipmentAddressSnapshotJson, InvoiceAddressSnapshotJson = remote.InvoiceAddressSnapshotJson, DerivedStatus = "NEW", CreatedAt = now, Version = 1 }; db.Orders.Add(order); }
        else if (remote.LastModifiedAt < order.LastRemoteModifiedAt) return;
        order.OrderNumber = remote.OrderNumber; order.Currency = remote.Currency; order.GrossAmount = remote.GrossAmount; order.DiscountAmount = remote.DiscountAmount; order.NetAmount = remote.NetAmount; order.OrderedAt = remote.OrderedAt; order.LastRemoteModifiedAt = remote.LastModifiedAt; order.CustomerSnapshotJson = remote.CustomerSnapshotJson; order.ShipmentAddressSnapshotJson = remote.ShipmentAddressSnapshotJson; order.InvoiceAddressSnapshotJson = remote.InvoiceAddressSnapshotJson; order.UpdatedAt = now; if (db.Entry(order).State != EntityState.Added) order.Version++;
        var lines = new Dictionary<string, OrderLine>(StringComparer.Ordinal);
        foreach (var remoteLine in remote.Lines)
        {
            var line = await db.OrderLines.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.OrderId == order.Id && x.ExternalLineId == remoteLine.ExternalLineId, cancellationToken); if (line is null) { line = new OrderLine { Id = Guid.CreateVersion7(), TenantId = tenantId, OrderId = order.Id, ExternalLineId = remoteLine.ExternalLineId, Sku = remoteLine.Sku, TitleSnapshot = remoteLine.Title, RawStatus = remoteLine.RawStatus, Version = 1 }; db.OrderLines.Add(line); }
            line.Sku = remoteLine.Sku; line.Barcode = remoteLine.Barcode; line.TitleSnapshot = remoteLine.Title; line.OrderedQuantity = remoteLine.Quantity; line.UnitPrice = remoteLine.UnitPrice; line.VatRate = remoteLine.VatRate; line.RawStatus = remoteLine.RawStatus; if (db.Entry(line).State != EntityState.Added) line.Version++; lines[remoteLine.ExternalLineId] = line;
        }
        foreach (var remotePackage in remote.Packages)
        {
            var target = CanonicalPackage(remotePackage.RawStatus); var eventId = PackageIngestionSafety.EventId(remotePackage.ExternalPackageId, remotePackage.OccurredAt); var orderedQuantities = lines.ToDictionary(x => x.Key, x => x.Value.OrderedQuantity, StringComparer.Ordinal);
            if (!PackageIngestionSafety.TryNormalizeAll(orderedQuantities, remotePackage.Allocations, target, out var safeAllocations)) { await RecordIssue(tenantId, $"package-quantity:{connectionId}:{eventId}", "PACKAGE_QUANTITY_INVARIANT_REJECTED", "Package miktarları sipariş satırı bütünlüğünü sağlamadı; olayın hiçbir parçası uygulanmadı.", cancellationToken); continue; }
            if (db.OrderStatusHistory.Local.Any(x => x.TenantId == tenantId && x.OrderId == order.Id && x.SourceEventId == eventId) || await db.OrderStatusHistory.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.OrderId == order.Id && x.SourceEventId == eventId, cancellationToken)) continue;
            var package = await db.ShipmentPackages.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalPackageId == remotePackage.ExternalPackageId, cancellationToken); var accept = package is null || PackageIngestionSafety.ShouldAccept(package.Status, package.StatusOccurredAt, target, remotePackage.OccurredAt);
            if (package is null) { package = new ShipmentPackage { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, OrderId = order.Id, ExternalPackageId = remotePackage.ExternalPackageId, Status = target, RawStatus = remotePackage.RawStatus, StatusOccurredAt = remotePackage.OccurredAt, CreatedAt = now, Version = 1 }; db.ShipmentPackages.Add(package); }
            else if (accept) { package.Status = target; package.RawStatus = remotePackage.RawStatus; package.StatusOccurredAt = remotePackage.OccurredAt; package.Version++; }
            else if (remotePackage.OccurredAt >= package.StatusOccurredAt && package.Status != target) await RecordIssue(tenantId, $"package-transition:{package.Id}:{remotePackage.RawStatus}", "PACKAGE_TRANSITION_REJECTED", "Out-of-order veya izin verilmeyen package geçişi mevcut durumu geriye götürmedi.", cancellationToken);
            if (accept)
            {
                package.OriginExternalPackageId = remotePackage.OriginExternalPackageId; package.CargoProviderExternalId = remotePackage.CargoProviderExternalId; package.CargoTrackingNumber = remotePackage.CargoTrackingNumber; package.GrossAmount = remotePackage.GrossAmount; package.DiscountAmount = remotePackage.DiscountAmount; package.NetAmount = remotePackage.NetAmount; package.UpdatedAt = now; db.OrderStatusHistory.Add(new OrderStatusHistory { Id = Guid.CreateVersion7(), TenantId = tenantId, OrderId = order.Id, PackageId = package.Id, CanonicalStatus = Wire(target), RawStatus = remotePackage.RawStatus, SourceEventId = eventId, OccurredAt = remotePackage.OccurredAt, RecordedAt = now });
                foreach (var remoteAllocation in remotePackage.Allocations) if (lines.TryGetValue(remoteAllocation.ExternalLineId, out var line) && safeAllocations.TryGetValue(remoteAllocation.ExternalLineId, out var safe)) { var allocation = await db.PackageLineAllocations.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.PackageId == package.Id && x.OrderLineId == line.Id && x.SourceEventId == eventId, cancellationToken); if (allocation is null) { allocation = new PackageLineAllocation { Id = Guid.CreateVersion7(), TenantId = tenantId, PackageId = package.Id, OrderLineId = line.Id, SourceEventId = eventId, AllocatedQuantity = safe.ActiveAllocatedQuantity, CancelledQuantity = safe.CancelledQuantity, ShippedQuantity = safe.ShippedQuantity, DeliveredQuantity = safe.DeliveredQuantity, ReturnedQuantity = safe.ReturnedQuantity }; db.PackageLineAllocations.Add(allocation); line.CancelledQuantity = Math.Max(line.CancelledQuantity, allocation.CancelledQuantity); line.ShippedQuantity = Math.Max(line.ShippedQuantity, allocation.ShippedQuantity); line.DeliveredQuantity = Math.Max(line.DeliveredQuantity, allocation.DeliveredQuantity); line.ReturnedQuantity = Math.Max(line.ReturnedQuantity, allocation.ReturnedQuantity); } }
            }
        }
        var persistedStatuses = await db.ShipmentPackages.AsNoTracking().Where(x => x.TenantId == tenantId && x.OrderId == order.Id).Select(x => new { x.Id, x.Status }).ToListAsync(cancellationToken); var acceptedStatuses = persistedStatuses.ToDictionary(x => x.Id, x => x.Status); foreach (var tracked in db.ShipmentPackages.Local.Where(x => x.TenantId == tenantId && x.OrderId == order.Id)) acceptedStatuses[tracked.Id] = tracked.Status; order.DerivedStatus = Wire(acceptedStatuses.Count == 0 ? ShipmentPackageStatus.New : acceptedStatuses.Values.OrderByDescending(StatusRank).First()); await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> SyncReturns(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        var cursor = await Cursor(tenantId, connectionId, "RETURNS", cancellationToken); var pageNumber = cursor.OpaqueCursor;
        do
        {
            var result = await returns.PollAsync(Context(tenantId, connectionId, correlationId, $"return-sync:{pageNumber}"), new(cursor.LastModifiedWatermark, null), new(pageNumber, 200), cancellationToken); if (!result.IsSuccess) throw JobProcessingException.FromAdapter(result.Error!); foreach (var claim in result.Value!.Items) await UpsertReturn(tenantId, connectionId, claim, cancellationToken); pageNumber = result.Value.NextCursor; cursor.OpaqueCursor = pageNumber; cursor.LastModifiedWatermark = result.Value.Items.Select(x => (DateTimeOffset?)x.LastModifiedAt).Max() ?? cursor.LastModifiedWatermark; cursor.LastSuccessAt = timeProvider.GetUtcNow(); cursor.Version++; await db.SaveChangesAsync(cancellationToken); if (!result.Value.HasMore) break;
        } while (!cancellationToken.IsCancellationRequested); return true;
    }

    private async Task UpsertReturn(Guid tenantId, Guid connectionId, RemoteReturnClaim remote, CancellationToken cancellationToken)
    {
        var order = await db.Orders.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && (x.ExternalOrderId == remote.ExternalOrderId || x.OrderNumber == remote.ExternalOrderId), cancellationToken);
        if (order is null)
        {
            await RecordIssue(tenantId, $"return-order:{connectionId}:{remote.ExternalOrderId}", "RETURN_ORDER_NOT_FOUND", "Return claim yerel order ile eşleşmedi; sessiz kayıt oluşturulmadı.", cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return;
        }
        var now = timeProvider.GetUtcNow(); var target = CanonicalReturn(remote.RawStatus); var claim = await db.ReturnClaims.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ExternalClaimId == remote.ExternalClaimId, cancellationToken);
        if (claim is null) { claim = new ReturnClaim { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, OrderId = order.Id, ExternalClaimId = remote.ExternalClaimId, Status = target, RawStatus = remote.RawStatus, LastRemoteModifiedAt = remote.LastModifiedAt, CreatedAt = now, UpdatedAt = now, Version = 1 }; db.ReturnClaims.Add(claim); }
        else { if (remote.LastModifiedAt < claim.LastRemoteModifiedAt || !ReturnClaimStateMachine.CanTransition(claim.Status, target)) return; claim.Status = target; claim.RawStatus = remote.RawStatus; claim.LastRemoteModifiedAt = remote.LastModifiedAt; claim.UpdatedAt = now; claim.Version++; }
        claim.ReasonCode = remote.ReasonCode; claim.ReasonText = remote.ReasonText; claim.ActionDueAt = remote.ActionDueAt;
        foreach (var remoteLine in remote.Lines) { var orderLine = await db.OrderLines.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.OrderId == order.Id && x.ExternalLineId == remoteLine.ExternalOrderLineId, cancellationToken); if (orderLine is null) continue; var line = await db.ReturnLines.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ClaimId == claim.Id && x.ExternalLineId == remoteLine.ExternalLineId, cancellationToken); if (line is null) db.ReturnLines.Add(new ReturnLine { Id = Guid.CreateVersion7(), TenantId = tenantId, ClaimId = claim.Id, OrderLineId = orderLine.Id, ExternalLineId = remoteLine.ExternalLineId, Quantity = remoteLine.Quantity }); else line.Quantity = remoteLine.Quantity; }
        var decision = await db.ReturnDecisions.Where(x => x.TenantId == tenantId && x.ClaimId == claim.Id && (x.Status == "PENDING" || x.Status == "SUBMITTED")).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (decision is not null)
        {
            var confirmed = decision.Action == "APPROVE" && target is ReturnClaimStatus.Approved or ReturnClaimStatus.Completed
                || decision.Action == "REJECT" && target is ReturnClaimStatus.Rejected or ReturnClaimStatus.Disputed;
            var conflictingTerminal = decision.Action == "APPROVE" && target is ReturnClaimStatus.Rejected or ReturnClaimStatus.Cancelled
                || decision.Action == "REJECT" && target is ReturnClaimStatus.Approved or ReturnClaimStatus.Completed;
            if (confirmed) { decision.Status = "SUCCEEDED"; decision.ErrorCode = null; decision.CompletedAt = now; }
            else if (conflictingTerminal) { decision.Status = "MANUAL_REVIEW"; decision.ErrorCode = "RETURN_ACTION_READBACK_CONFLICT"; decision.CompletedAt = now; }
            else decision.Status = "SUBMITTED";
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> ShipmentAction(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        ShipmentActionJobPayload? payload;
        try { payload = JsonSerializer.Deserialize<ShipmentActionJobPayload>(payloadJson, JsonOptions); }
        catch (JsonException) { return false; }
        if (payload is null || payload.JobId == Guid.Empty || payload.PackageId == Guid.Empty || string.IsNullOrWhiteSpace(payload.Action)) return false;
        var job = await db.IntegrationJobs.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == payload.JobId && x.ConnectionId == connectionId && x.JobType == F3JobTypes.ShipmentAction, cancellationToken);
        var package = await db.ShipmentPackages.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == payload.PackageId && x.ConnectionId == connectionId, cancellationToken);
        if (job is null || package is null) return false;
        var effect = await db.ExternalEffectRecords.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.EffectType == F3JobTypes.ShipmentAction && x.IdempotencyKey == job.EffectIdempotencyKey, cancellationToken);
        if (effect is not null && effect.CompletedAt is null) throw new JobProcessingException(JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS", "Önceki paket aksiyonunun sonucu kesinleştirilemedi; tekrar gönderim engellendi."));
        if (effect is not null && effect.CompletedAt is not null) return true;
        effect = new ExternalEffectRecord { Id = Guid.CreateVersion7(), TenantId = tenantId, EffectType = F3JobTypes.ShipmentAction, IdempotencyKey = job.EffectIdempotencyKey, CreatedAt = timeProvider.GetUtcNow() };
        db.ExternalEffectRecords.Add(effect); await db.SaveChangesAsync(cancellationToken);
        var result = await orders.ExecutePackageActionAsync(Context(tenantId, connectionId, correlationId, job.EffectIdempotencyKey), new(package.ExternalPackageId, payload.Action, payload.PayloadJson), cancellationToken);
        if (!result.IsSuccess)
        {
            if (IsAmbiguous(result.Error!)) throw new JobProcessingException(JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS", "Paket aksiyonunun uzak tarafta uygulanıp uygulanmadığı kesinleştirilemedi.", result.Error!.RemoteRequestId));
            db.ExternalEffectRecords.Remove(effect); await db.SaveChangesAsync(cancellationToken); throw JobProcessingException.FromAdapter(result.Error!);
        }
        effect.CompletedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken);
        var orderNumber = await db.Orders.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == package.OrderId).Select(x => x.OrderNumber).SingleAsync(cancellationToken);
        var readback = await orders.GetAsync(Context(tenantId, connectionId, correlationId, $"{job.EffectIdempotencyKey}:readback"), orderNumber, cancellationToken);
        if (readback.IsSuccess) await UpsertOrder(tenantId, connectionId, readback.Value!, cancellationToken);
        else { await RecordIssue(tenantId, $"shipment-readback:{connectionId}:{package.ExternalPackageId}:{payload.Action}", "SHIPMENT_ACTION_READBACK_PENDING", "Paket aksiyonu kabul edildi ancak anlık order read-back tamamlanamadı; planlı sync kesinleştirecek.", cancellationToken); await db.SaveChangesAsync(cancellationToken); }
        return true;
    }

    private async Task<bool> ReturnAction(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        try
        {
            using var payload = JsonDocument.Parse(payloadJson);
            var decisionId = payload.RootElement.GetProperty("decisionId").GetGuid();
            var decision = await db.ReturnDecisions.SingleAsync(x => x.TenantId == tenantId && x.Id == decisionId, cancellationToken);
            if (decision.Status == "SUCCEEDED") return true;
            if (decision.Status == "MANUAL_REVIEW") throw new JobProcessingException(JobExecutionResult.ManualReview(decision.ErrorCode ?? "RETURN_ACTION_REVIEW_REQUIRED", "İade kararı manuel inceleme bekliyor.", decision.ExternalOperationId));
            var claim = await db.ReturnClaims.AsNoTracking().SingleAsync(x => x.TenantId == tenantId && x.Id == decision.ClaimId && x.ConnectionId == connectionId, cancellationToken);
            var lineIds = await db.ReturnLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.ClaimId == claim.Id).OrderBy(x => x.Id).Select(x => x.ExternalLineId).ToListAsync(cancellationToken);
            if (lineIds.Count == 0) { decision.Status = "FAILED"; decision.ErrorCode = "RETURN_LINES_REQUIRED"; decision.CompletedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); return false; }
            var evidenceRows = await (from evidence in db.ReturnEvidence.AsNoTracking() where evidence.TenantId == tenantId && evidence.DecisionId == decisionId join asset in db.FileAssets.AsNoTracking() on new { evidence.TenantId, Id = evidence.FileAssetId } equals new { asset.TenantId, asset.Id } select asset).ToListAsync(cancellationToken);
            var evidenceFiles = new List<ReturnEvidenceFile>(); long totalBytes = 0;
            foreach (var asset in evidenceRows)
            {
                if (asset.SizeBytes <= 0 || asset.SizeBytes > 10 * 1024 * 1024 || totalBytes + asset.SizeBytes > 25 * 1024 * 1024) { decision.Status = "FAILED"; decision.ErrorCode = "RETURN_EVIDENCE_LIMIT_EXCEEDED"; decision.CompletedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); return false; }
                await using var source = await files.OpenReadAsync(tenantId, asset.RelativePath, cancellationToken); await using var buffer = new MemoryStream(); await source.CopyToAsync(buffer, cancellationToken); if (buffer.Length != asset.SizeBytes) { decision.Status = "MANUAL_REVIEW"; decision.ErrorCode = "RETURN_EVIDENCE_SIZE_MISMATCH"; decision.CompletedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); throw new JobProcessingException(JobExecutionResult.ManualReview(decision.ErrorCode, "İade kanıt dosyasının kayıtlı boyutu ile okunan içerik eşleşmedi.")); }
                var bytes = buffer.ToArray(); var checksum = Convert.ToHexString(SHA256.HashData(bytes)); if (!string.Equals(checksum, asset.Sha256, StringComparison.OrdinalIgnoreCase)) { decision.Status = "MANUAL_REVIEW"; decision.ErrorCode = "RETURN_EVIDENCE_CHECKSUM_MISMATCH"; decision.CompletedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); throw new JobProcessingException(JobExecutionResult.ManualReview(decision.ErrorCode, "İade kanıt dosyasının checksum doğrulaması başarısız oldu.")); }
                evidenceFiles.Add(new(asset.OriginalNameSafe ?? $"evidence-{asset.Id:N}", asset.MimeType, bytes)); totalBytes += bytes.LongLength;
            }
            var existingEffect = await db.ExternalEffectRecords.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.EffectType == F3JobTypes.ReturnAction && x.IdempotencyKey == decision.IdempotencyKey, cancellationToken);
            if (existingEffect is not null && existingEffect.CompletedAt is null) { decision.Status = "MANUAL_REVIEW"; decision.ErrorCode = "EXTERNAL_EFFECT_AMBIGUOUS"; decision.CompletedAt = timeProvider.GetUtcNow(); await db.SaveChangesAsync(cancellationToken); throw new JobProcessingException(JobExecutionResult.ManualReview(decision.ErrorCode, "Önceki iade aksiyonunun sonucu kesinleştirilemedi; tekrar gönderim engellendi.")); }
            if (existingEffect is not null && existingEffect.CompletedAt is not null)
            {
                decision.Status = "SUBMITTED"; decision.CompletedAt = null; await db.SaveChangesAsync(cancellationToken);
                var readback = await returns.GetAsync(Context(tenantId, connectionId, correlationId, $"{decision.IdempotencyKey}:readback"), claim.ExternalClaimId, cancellationToken);
                if (readback.IsSuccess) await UpsertReturn(tenantId, connectionId, readback.Value!, cancellationToken);
                else { await RecordIssue(tenantId, $"return-readback:{connectionId}:{claim.ExternalClaimId}:{decision.Action}", "RETURN_ACTION_READBACK_PENDING", "İade aksiyonu daha önce kabul edildi ancak read-back tamamlanamadı; planlı return sync kesinleştirecek.", cancellationToken); await db.SaveChangesAsync(cancellationToken); }
                return true;
            }
            var effect = new ExternalEffectRecord { Id = Guid.CreateVersion7(), TenantId = tenantId, EffectType = F3JobTypes.ReturnAction, IdempotencyKey = decision.IdempotencyKey, CreatedAt = timeProvider.GetUtcNow() }; db.ExternalEffectRecords.Add(effect); await db.SaveChangesAsync(cancellationToken);
            var result = await returns.ExecuteAsync(Context(tenantId, connectionId, correlationId, decision.IdempotencyKey), new(claim.ExternalClaimId, lineIds, decision.Action, decision.ReasonCode, decision.Explanation, evidenceFiles), cancellationToken);
            var now = timeProvider.GetUtcNow();
            if (result.IsSuccess)
            {
                effect.CompletedAt = now; decision.Status = "SUBMITTED"; decision.ExternalOperationId = result.Value!.ExternalOperationId; decision.ErrorCode = null; decision.CompletedAt = null; await db.SaveChangesAsync(cancellationToken);
                var readback = await returns.GetAsync(Context(tenantId, connectionId, correlationId, $"{decision.IdempotencyKey}:readback"), claim.ExternalClaimId, cancellationToken);
                if (readback.IsSuccess) await UpsertReturn(tenantId, connectionId, readback.Value!, cancellationToken);
                else { await RecordIssue(tenantId, $"return-readback:{connectionId}:{claim.ExternalClaimId}:{decision.Action}", "RETURN_ACTION_READBACK_PENDING", "İade aksiyonu kabul edildi ancak anlık read-back tamamlanamadı; planlı return sync kesinleştirecek.", cancellationToken); await db.SaveChangesAsync(cancellationToken); }
                return true;
            }
            var error = result.Error!; decision.ErrorCode = error.Code; decision.ExternalOperationId ??= error.RemoteRequestId;
            if (IsAmbiguous(error)) { decision.Status = "MANUAL_REVIEW"; decision.CompletedAt = now; await db.SaveChangesAsync(cancellationToken); throw new JobProcessingException(JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS", "İade aksiyonunun uzak tarafta uygulanıp uygulanmadığı kesinleştirilemedi.", error.RemoteRequestId)); }
            db.ExternalEffectRecords.Remove(effect); decision.Status = "FAILED"; decision.CompletedAt = now; await db.SaveChangesAsync(cancellationToken); throw JobProcessingException.FromAdapter(error);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException) { return false; }
    }

    private async Task<SyncCursor> Cursor(Guid tenantId, Guid connectionId, string resource, CancellationToken cancellationToken) { var cursor = await db.SyncCursors.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == resource, cancellationToken); if (cursor is not null) return cursor; cursor = new SyncCursor { Id = Guid.CreateVersion7(), TenantId = tenantId, ConnectionId = connectionId, ResourceType = resource, Version = 1 }; db.SyncCursors.Add(cursor); return cursor; }
    private async Task RecordIssue(Guid tenantId, string key, string code, string summary, CancellationToken cancellationToken) { var now = timeProvider.GetUtcNow(); var issue = await db.OperationalIssues.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.DedupeKey == key, cancellationToken); if (issue is null) db.OperationalIssues.Add(new OperationalIssue { Id = Guid.CreateVersion7(), TenantId = tenantId, DedupeKey = key, Code = code, Summary = summary, Status = IssueStatus.Open, FirstSeenAt = now, LastSeenAt = now, OccurrenceCount = 1 }); else { issue.LastSeenAt = now; issue.OccurrenceCount++; } }
    private AdapterContext Context(Guid tenantId, Guid connectionId, string correlationId, string idempotency) => new(tenantId, connectionId, correlationId, idempotency, timeProvider.GetUtcNow().AddMinutes(2));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static ShipmentPackageStatus CanonicalPackage(string raw) => raw.ToUpperInvariant() switch { "CREATED" => ShipmentPackageStatus.New, "PICKING" => ShipmentPackageStatus.Processing, "INVOICED" => ShipmentPackageStatus.ReadyToShip, "SHIPPED" => ShipmentPackageStatus.Shipped, "DELIVERED" => ShipmentPackageStatus.Delivered, "CANCELLED" or "UNSUPPLIED" => ShipmentPackageStatus.Cancelled, "UNDELIVERED" => ShipmentPackageStatus.Undelivered, "RETURNED" => ShipmentPackageStatus.Returned, "AWAITING" or "UNPACKED" or "AT_COLLECTION_POINT" => ShipmentPackageStatus.OnHold, _ => ShipmentPackageStatus.ManualReview };
    private static ReturnClaimStatus CanonicalReturn(string raw) => raw.ToUpperInvariant() switch { "CREATED" => ReturnClaimStatus.Requested, "WAITINGINACTION" or "INANALYSIS" or "WAITINGFRAUDCHECK" => ReturnClaimStatus.ActionRequired, "ACCEPTED" => ReturnClaimStatus.Approved, "REJECTED" => ReturnClaimStatus.Rejected, "UNRESOLVED" => ReturnClaimStatus.Disputed, "COMPLETED" => ReturnClaimStatus.Completed, "CANCELLED" => ReturnClaimStatus.Cancelled, _ => ReturnClaimStatus.ActionRequired };
    private static int StatusRank(ShipmentPackageStatus status) => status switch { ShipmentPackageStatus.New => 1, ShipmentPackageStatus.Processing => 2, ShipmentPackageStatus.OnHold => 3, ShipmentPackageStatus.ReadyToShip => 4, ShipmentPackageStatus.Shipped => 5, ShipmentPackageStatus.Undelivered => 6, ShipmentPackageStatus.Delivered => 7, ShipmentPackageStatus.ReturnInTransit => 8, ShipmentPackageStatus.Returned => 9, ShipmentPackageStatus.PartiallyCancelled => 2, ShipmentPackageStatus.Cancelled => 9, _ => 10 };
    private static string Wire<T>(T value) where T : Enum => string.Concat(value.ToString().Select((ch, index) => char.IsUpper(ch) && index > 0 ? "_" + ch : ch.ToString())).ToUpperInvariant();
}
