using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using MarketplaceHub.Infrastructure.Adapters.Trendyol.Mapping;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed class F3JobProcessor(AppDbContext db, IConnectionPort connections, IReferenceDataPort references, IProductPort products, IOrderPort orders, IReturnPort returns, TimeProvider timeProvider) : IF3JobProcessor
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
            var succeeded = jobType switch
            {
                F3JobTypes.ConnectionTest => await TestConnection(tenantId, connectionId.Value, correlationId, cancellationToken),
                F3JobTypes.ReferenceSync => await SyncReferences(tenantId, connectionId.Value, payloadJson, correlationId, cancellationToken),
                F3JobTypes.OrderSync => await SyncOrders(tenantId, connectionId.Value, correlationId, cancellationToken),
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

        var candidates = listings.Where(x => !string.Equals(x.ActualStatus, "CREATE_REJECTED", StringComparison.Ordinal)).ToList();
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
        string? firstCode = listings.Where(x => string.Equals(x.ActualStatus, "CREATE_REJECTED", StringComparison.Ordinal)).Select(x => x.RejectionCode).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

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
        var candidates = listings.Where(x => !string.Equals(x.ActualStatus, "CREATE_REJECTED", StringComparison.Ordinal)).ToList();
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
            Id = jobId, TenantId = tenantId, ConnectionId = connectionId, JobType = F3JobTypes.ProductApprovalReconcile, PayloadJson = payload, PayloadVersion = 1,
            PayloadHash = Hash(payload), JobDedupKey = dedup, EffectIdempotencyKey = dedup, Priority = 1, Status = JobStatus.Pending,
            AvailableAt = now, MaxAttempts = 200, CorrelationId = correlationId, CreatedAt = now, Version = 1
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


    private async Task<bool> SyncOrders(Guid tenantId, Guid connectionId, string correlationId, CancellationToken cancellationToken)
    {
        var cursor = await Cursor(tenantId, connectionId, "ORDERS", cancellationToken); var policy = await db.ConnectionSyncPolicies.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == "ORDERS", cancellationToken); var modifiedAfter = cursor.LastModifiedWatermark?.Subtract(TimeSpan.FromSeconds(policy?.OverlapSeconds ?? 0)); var next = cursor.OpaqueCursor; var orderPort = orders;
        do
        {
            var result = await orderPort.PollAsync(Context(tenantId, connectionId, correlationId, $"order-sync:{next}"), new(modifiedAfter, null), new(next, 200), cancellationToken); if (!result.IsSuccess) throw JobProcessingException.FromAdapter(result.Error!);
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
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> ShipmentAction(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        try { using var payload = JsonDocument.Parse(payloadJson); var packageId = payload.RootElement.GetProperty("packageId").GetGuid(); var action = payload.RootElement.GetProperty("Action").GetString()!; var body = payload.RootElement.GetProperty("PayloadJson").GetString()!; var package = await db.ShipmentPackages.AsNoTracking().SingleAsync(x => x.TenantId == tenantId && x.Id == packageId && x.ConnectionId == connectionId, cancellationToken); var result = await orders.ExecutePackageActionAsync(Context(tenantId, connectionId, correlationId, $"shipment:{packageId}:{action}"), new(package.ExternalPackageId, action, body), cancellationToken); if (!result.IsSuccess) throw JobProcessingException.FromAdapter(result.Error!); return true; } catch (Exception exception) when (exception is JsonException or InvalidOperationException) { return false; }
    }

    private async Task<bool> ReturnAction(Guid tenantId, Guid connectionId, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        try
        {
            using var payload = JsonDocument.Parse(payloadJson);
            var decisionId = payload.RootElement.GetProperty("decisionId").GetGuid();
            var decision = await db.ReturnDecisions.SingleAsync(x => x.TenantId == tenantId && x.Id == decisionId, cancellationToken);
            if (decision.Status == "SUCCEEDED") return true;
            if (decision.Status == "MANUAL_REVIEW")
                throw new JobProcessingException(JobExecutionResult.ManualReview(decision.ErrorCode ?? "RETURN_ACTION_REVIEW_REQUIRED", "İade kararı manuel inceleme bekliyor.", decision.ExternalOperationId));

            var claim = await db.ReturnClaims.AsNoTracking().SingleAsync(x => x.TenantId == tenantId && x.Id == decision.ClaimId && x.ConnectionId == connectionId, cancellationToken);
            var evidence = await db.ReturnEvidence.AsNoTracking().Where(x => x.TenantId == tenantId && x.DecisionId == decisionId).Select(x => x.FileAssetId).ToListAsync(cancellationToken);
            var result = await returns.ExecuteAsync(Context(tenantId, connectionId, correlationId, decision.IdempotencyKey), new(claim.ExternalClaimId, decision.Action, decision.ReasonCode, decision.Explanation, evidence), cancellationToken);
            var now = timeProvider.GetUtcNow();
            if (result.IsSuccess)
            {
                decision.Status = "SUCCEEDED";
                decision.ExternalOperationId = result.Value!.ExternalOperationId;
                decision.ErrorCode = null;
                decision.CompletedAt = now;
                await db.SaveChangesAsync(cancellationToken);
                return true;
            }

            var error = result.Error!;
            decision.ErrorCode = error.Code;
            decision.ExternalOperationId ??= error.RemoteRequestId;
            decision.Status = error.Class switch
            {
                AdapterErrorClass.TransientNetwork or AdapterErrorClass.RateLimit or AdapterErrorClass.Remote5xx => "RETRY_SCHEDULED",
                AdapterErrorClass.ContractViolation or AdapterErrorClass.InternalBug => "MANUAL_REVIEW",
                _ => "FAILED"
            };
            decision.CompletedAt = decision.Status == "RETRY_SCHEDULED" ? null : now;
            await db.SaveChangesAsync(cancellationToken);
            throw JobProcessingException.FromAdapter(error);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return false;
        }
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
