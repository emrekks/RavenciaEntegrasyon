using System.Data;
using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

public sealed partial class CatalogService
{
    // "Platformlarda Güncelle" on PTT AVM: a ProductUpdate job with one phase (SUBMIT_UNAPPROVED, the PTT AVM update
    // payload of PttAvmProductUpdateComposer). It is a manual write like the publication: the write switches, the
    // connection's MANUAL_WRITE policy (PttAvmManualWritePolicy) and a ready PTT AVM connection are checked here and
    // again by the worker and the adapter. The picked fields narrow the request (PTT AVM keeps a field left out).
    private async Task<ServiceResult<Guid>> EnqueuePttAvmProductUpdateAsync(Guid tenantId, Guid productId, Guid connectionId, string idempotencyKey, string correlationId, IReadOnlyCollection<string>? fields, CancellationToken cancellationToken)
    {
        var picked = PttAvmProductUpdateFields.Parse(fields);
        if (!picked.Succeeded) return ServiceResult<Guid>.Fail(picked.Error!.Code, picked.Error.Message, picked.Error.Status);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var connection = await db.PlatformConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == connectionId && x.PlatformCode == "PTTAVM", cancellationToken);
        if (connection is null || !IntegrationRuntimePolicy.IsManualProductWriteReady(connection)) return ServiceResult<Guid>.Fail("ACTIVE_CONNECTION_REQUIRED", "Güncelleme için ACTIVE veya doğrulanmış PTT AVM bağlantısı gerekir.", 422);
        if (!IntegrationRuntimePolicy.IsSupportedEnvironment(connection)) return ServiceResult<Guid>.Fail("ENVIRONMENT_INVALID", "Güncelleme yalnız STAGE veya PRODUCTION bağlantısında çalışır.", 422);
        if (!WritesEnabled(connection.SettingsJson)) return ServiceResult<Guid>.Fail("EXTERNAL_WRITES_DISABLED", "PTT AVM'de ürün güncellemek için global ve bağlantı dış yazma anahtarları açık olmalıdır.", 422);
        var manualPolicy = await db.ConnectionSyncPolicies.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.ResourceType == MarketplaceExternalWritePolicies.Manual)
            .Select(x => (bool?)x.Enabled)
            .SingleOrDefaultAsync(cancellationToken);
        if (PttAvmManualWritePolicy.Decide(masterWritesEnabled: true, manualPolicy) != PttAvmManualWriteDecision.Allowed)
            return ServiceResult<Guid>.Fail(PttAvmManualWritePolicy.DisabledCode, PttAvmManualWritePolicy.DisabledMessage, 422);

        var build = await new PttAvmProductUpdateComposer(db, configuration).BuildAsync(tenantId, productId, connectionId, picked.Value!, cancellationToken);
        if (!build.Succeeded) return ServiceResult<Guid>.Fail(build.Error!.Code, build.Error.Message, build.Error.Status, build.Error.FieldErrors);
        var draft = build.Value!;
        var dedup = $"product-update:{connectionId:N}:{productId:N}:{draft.Publication.PayloadHash}";
        var existing = await db.IntegrationJobs.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.JobType == MarketplaceJobTypes.ProductUpdate && x.JobDedupKey == dedup, cancellationToken);
        if (existing is not null && await ReuseOrRetirePublicationJobAsync(existing, dedup, reuseSucceeded: false, cancellationToken)) { await transaction.CommitAsync(cancellationToken); return ServiceResult<Guid>.Ok(existing.Id); }

        var profile = await db.ChannelListingProfiles.SingleAsync(x => x.TenantId == tenantId && x.Id == draft.ProfileId, cancellationToken);
        profile.DesiredStatus = "LIVE"; profile.ActualStatus = "UPDATE_QUEUED"; profile.LastRejectionCode = null; profile.Version++;
        // The job reports on every listing of the card, so every listing's state carries this update's hash (an older
        // job of the card then stops as superseded).
        var listingVariantIds = await db.ChannelListingVariants.AsNoTracking().Where(x => x.TenantId == tenantId && x.ProfileId == profile.Id).Select(x => x.VariantId).ToListAsync(cancellationToken);
        var states = await db.MarketplaceListingStates.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && listingVariantIds.Contains(x.VariantId)).ToListAsync(cancellationToken);
        if (states.Count != listingVariantIds.Count) return ServiceResult<Guid>.Fail("PRODUCT_UPDATE_STATE_INCOMPLETE", "Kartın PTT AVM yayın kayıtları eksik; önce yayın durumunu yenileyin.", 409);
        foreach (var state in states) { state.DesiredStatus = "LIVE"; state.PayloadHash = draft.Publication.PayloadHash; state.Version++; }

        var jobId = Guid.CreateVersion7();
        var now = timeProvider.GetUtcNow();
        var payload = JsonSerializer.Serialize(new ProductUpdateJobPayload(jobId, productId, profile.Id, "SUBMIT_UNAPPROVED", draft.Publication.Mode, draft.Publication.PayloadHash, draft.Publication.UnapprovedPayloadJson, "", "", "", null, null));
        db.IntegrationJobs.Add(new IntegrationJob { Id = jobId, TenantId = tenantId, ConnectionId = connectionId, JobType = MarketplaceJobTypes.ProductUpdate, PayloadJson = payload, PayloadVersion = 1, PayloadHash = Hash(payload), JobDedupKey = dedup, EffectIdempotencyKey = $"{dedup}:{NormalizeKey(idempotencyKey)}", Priority = JobPriorityPolicy.For(MarketplaceJobTypes.ProductUpdate, userInitiated: true), Status = JobStatus.Pending, AvailableAt = now, MaxAttempts = 12, CorrelationId = correlationId, CreatedAt = now, Version = 1 });
        return await SavePublicationJobAsync(transaction, tenantId, MarketplaceJobTypes.ProductUpdate, dedup, jobId, cancellationToken);
    }
}
