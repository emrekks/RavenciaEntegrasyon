using System.Text.Json;
using MarketplaceHub.Application;
using MarketplaceHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace MarketplaceHub.Infrastructure.Persistence;

// The grouped Trendyol send of one "Platformlarda Güncelle" (TrendyolUpdateGroups): the cards' jobs share a request per
// phase and one poll of its batch id. Product jobs of a connection run one at a time under its product lock, so while a
// card sends or polls for the group no other card's job runs; the other cards' payloads are written in place (their
// lease and attempt columns stay their runner's) and each card's own next run carries on from what was written.
public sealed partial class MarketplaceJobProcessor
{
    private sealed record GroupCard(IntegrationJob Job, ProductUpdateJobPayload Payload);

    // The other cards of the send still running on this connection, in the order they were queued.
    private async Task<List<GroupCard>> LoadSendGroupAsync(Guid tenantId, Guid connectionId, Guid groupId, Guid selfJobId, CancellationToken cancellationToken)
    {
        var marker = groupId.ToString("D");
        var jobs = await db.IntegrationJobs.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && x.JobType == MarketplaceJobTypes.ProductUpdate && x.Id != selfJobId
                && (x.Status == JobStatus.Pending || x.Status == JobStatus.Leased || x.Status == JobStatus.RetryScheduled)
                && x.PayloadJson.Contains(marker))
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var cards = new List<GroupCard>();
        foreach (var job in jobs)
        {
            ProductUpdateJobPayload? payload;
            try { payload = JsonSerializer.Deserialize<ProductUpdateJobPayload>(job.PayloadJson, JsonOptions); }
            catch (JsonException) { continue; }
            if (payload?.SendGroupId == groupId && payload.JobId == job.Id) cards.Add(new(job, payload));
        }
        return cards;
    }

    // Null: the card goes alone (no other card of its send is ready and none is on its way), on the usual path.
    private async Task<JobExecutionResult?> SubmitTrendyolGroupAsync(Guid tenantId, Guid connectionId, IntegrationJob job, ProductUpdateJobPayload payload, ChannelListingProfile profile, string phase, string correlationId, CancellationToken cancellationToken)
    {
        var groupId = payload.SendGroupId!.Value;
        var siblings = await LoadSendGroupAsync(tenantId, connectionId, groupId, job.Id, cancellationToken);
        if (siblings.Count == 0) return null;
        var now = timeProvider.GetUtcNow();
        if (payload.GroupReadySince is null)
        {
            payload = payload with { GroupReadySince = now };
            SetUpdatePayload(job, payload);
            await db.SaveChangesAsync(cancellationToken);
        }
        var behind = siblings.Count(card => TrendyolUpdateGroups.IsBehind(card.Payload, phase));
        if (behind > 0 && now - payload.GroupReadySince!.Value < TrendyolUpdateGroups.ReadyWait)
            return JobExecutionResult.Retry(TrendyolUpdateGroups.WaitingCode, $"Aynı toplu işlemdeki {behind} kart daha bekleniyor; kartlar Trendyol'a tek istekte gidecek.", TimeSpan.FromSeconds(3));

        // A ready card whose effect record exists already, or whose card was updated again since, is left to its own run:
        // that run stops it (an unclear earlier request, or a newer update) without sending.
        var effectType = $"{MarketplaceJobTypes.ProductUpdate}:{phase}";
        var candidates = new List<GroupCard> { new(job, payload) };
        foreach (var card in siblings.Where(card => TrendyolUpdateGroups.IsReady(card.Payload, phase)))
        {
            var key = $"{card.Job.EffectIdempotencyKey}:{phase}";
            if (await db.ExternalEffectRecords.AnyAsync(x => x.TenantId == tenantId && x.EffectType == effectType && x.IdempotencyKey == key, cancellationToken)) continue;
            if (await ProductUpdateSupersededAsync(tenantId, connectionId, card.Payload, cancellationToken)) continue;
            candidates.Add(card);
        }
        var selected = TrendyolUpdateGroups.Select(candidates.Select(card => (card, UpdatePayload(card.Payload, phase))).ToList());
        if (selected.Count < 2) return null;

        var effects = selected.Select(card => new ExternalEffectRecord { Id = Guid.CreateVersion7(), TenantId = tenantId, EffectType = effectType, IdempotencyKey = $"{card.Job.EffectIdempotencyKey}:{phase}", CreatedAt = now }).ToList();
        db.ExternalEffectRecords.AddRange(effects);
        await db.SaveChangesAsync(cancellationToken);

        var merged = TrendyolUpdateGroups.Merge(selected.Select(card => UpdatePayload(card.Payload, phase)));
        var publication = new ProductUpdatePublication(payload.ProductId, payload.Mode, payload.PayloadHash,
            phase == "SUBMIT_UNAPPROVED" ? merged : payload.UnapprovedPayloadJson,
            phase == "SUBMIT_CONTENT" ? merged : payload.ApprovedContentPayloadJson,
            phase == "SUBMIT_VARIANTS" ? merged : payload.ApprovedVariantPayloadJson,
            phase == "SUBMIT_DELIVERY" ? merged : payload.ApprovedDeliveryPayloadJson);
        var context = Context(tenantId, connectionId, correlationId, $"{job.EffectIdempotencyKey}:{phase}");
        TrackRequest();
        var submit = phase switch
        {
            "SUBMIT_UNAPPROVED" => await products.UpdateUnapprovedAsync(context, publication, cancellationToken),
            "SUBMIT_CONTENT" => await products.UpdateApprovedContentAsync(context, publication, cancellationToken),
            "SUBMIT_VARIANTS" => await products.UpdateApprovedVariantsAsync(context, publication, cancellationToken),
            "SUBMIT_DELIVERY" => await products.UpdateApprovedDeliveryAsync(context, publication, cancellationToken),
            _ => AdapterResult<RemoteOperationRef>.Failure(new(AdapterErrorClass.ContractViolation, "PRODUCT_UPDATE_PHASE_INVALID", "Ürün güncelleme fazı tanınmıyor.", null, null, null))
        };
        var others = selected.Skip(1).ToList();
        if (!submit.IsSuccess)
        {
            TrackResultFailure(submit.Error);
            var error = submit.Error!;
            if (IsAmbiguous(error))
            {
                // Every card of the request keeps its effect record: its own run stops it for review, none is sent again.
                await WakeJobsAsync(others.Select(card => card.Job.Id).ToList(), cancellationToken);
                return await MarkUpdateResult(tenantId, connectionId, profile, "MANUAL_REVIEW", "EXTERNAL_EFFECT_AMBIGUOUS", JobExecutionResult.ManualReview("EXTERNAL_EFFECT_AMBIGUOUS", $"{selected.Count} kartın ortak ürün güncelleme çağrısının uygulanıp uygulanmadığı kesinleştirilemedi.", error.RemoteRequestId), cancellationToken);
            }
            db.ExternalEffectRecords.RemoveRange(effects);
            await db.SaveChangesAsync(cancellationToken);
            var failure = JobExecutionResult.FromAdapterError(error);
            // A refusal is every card's; a passing error (quota, a busy service) leaves the others ready for the next send.
            if (failure.Kind != JobCompletionKind.Retry)
                await StopGroupCardsAsync(tenantId, others, TrendyolUpdateGroups.Outcome(failure, "UPDATE_BLOCKED", error.Code), cancellationToken);
            return await MarkUpdateResult(tenantId, connectionId, profile, "UPDATE_BLOCKED", error.Code, failure, cancellationToken);
        }

        var operation = submit.Value!;
        var completedAt = timeProvider.GetUtcNow();
        foreach (var effect in effects) effect.CompletedAt = completedAt;
        var shared = new ProductUpdateSharedRequest(TrendyolUpdateGroups.Part(phase), operation.ExternalOperationId, selected.Count, TrendyolUpdateGroups.ItemCount(merged), job.Id);
        var pollPhase = phase.Replace("SUBMIT_", "POLL_", StringComparison.Ordinal);
        var submittedStatus = phase.Replace("SUBMIT_", "UPDATE_", StringComparison.Ordinal) + "_SUBMITTED";
        ProductUpdateJobPayload Polling(ProductUpdateJobPayload card) => card with
        {
            Phase = pollPhase,
            ExternalOperationId = operation.ExternalOperationId,
            SubmittedAt = operation.SubmittedAt,
            GroupReadySince = null,
            GroupLeaderJobId = job.Id,
            SharedRequests = [.. card.SharedRequests ?? [], shared]
        };
        SetUpdatePayload(job, Polling(payload));
        profile.ActualStatus = submittedStatus; profile.Version++;
        foreach (var card in others)
        {
            await WriteGroupCardPayloadAsync(card, Polling(card.Payload), cancellationToken);
            var cardProfile = await db.ChannelListingProfiles.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == card.Payload.ProfileId, cancellationToken);
            if (cardProfile is not null) { cardProfile.ActualStatus = submittedStatus; cardProfile.Version++; }
        }
        await db.SaveChangesAsync(cancellationToken);
        return JobExecutionResult.Retry("PRODUCT_UPDATE_BATCH_PENDING", ProductUpdatePendingMessage(false, false), ProductUpdatePollDelay(operation.SubmittedAt), operation.ExternalOperationId);
    }

    // A card that went in a shared request: the sending card polls the batch id once for all; a card whose sender is no
    // longer running (stopped, cancelled) polls in its place.
    private async Task<JobExecutionResult> PollTrendyolGroupAsync(Guid tenantId, Guid connectionId, IntegrationJob job, ProductUpdateJobPayload payload, ChannelListingProfile profile, IReadOnlyList<ChannelListingVariant> listings, IReadOnlyDictionary<Guid, MarketplaceListingState> states, string phase, string correlationId, CancellationToken cancellationToken)
    {
        var leaderId = payload.GroupLeaderJobId!.Value;
        if (leaderId != job.Id)
        {
            var leader = await db.IntegrationJobs.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == leaderId)
                .Select(x => new { x.Status, x.PayloadJson }).SingleOrDefaultAsync(cancellationToken);
            ProductUpdateJobPayload? leaderPayload = null;
            if (leader is not null)
                try { leaderPayload = JsonSerializer.Deserialize<ProductUpdateJobPayload>(leader.PayloadJson, JsonOptions); }
                catch (JsonException) { leaderPayload = null; }
            // The sender still waits on this request: it polls. Otherwise (it stopped, or moved on without answering this
            // card) this card polls in its place.
            if (leader is not null && leader.Status is JobStatus.Pending or JobStatus.Leased or JobStatus.RetryScheduled
                && leaderPayload is not null && leaderPayload.Phase == phase && leaderPayload.ExternalOperationId == payload.ExternalOperationId)
                return JobExecutionResult.Retry("PRODUCT_UPDATE_BATCH_PENDING", "Bu kart diğer kartlarla aynı Trendyol isteğinde gitti; sonucu o isteği gönderen iş okuyor.", TimeSpan.FromSeconds(60), payload.ExternalOperationId);
        }
        var others = (await LoadSendGroupAsync(tenantId, connectionId, payload.SendGroupId!.Value, job.Id, cancellationToken))
            .Where(card => card.Payload.Phase == phase && card.Payload.ExternalOperationId == payload.ExternalOperationId)
            .ToList();

        async Task<JobExecutionResult> StopAll(string profileStatus, string? rejectionCode, JobExecutionResult result)
        {
            await StopGroupCardsAsync(tenantId, others, TrendyolUpdateGroups.Outcome(result, profileStatus, rejectionCode), cancellationToken);
            return await MarkUpdateResult(tenantId, connectionId, profile, profileStatus, rejectionCode, result, cancellationToken);
        }

        if (timeProvider.GetUtcNow() - payload.SubmittedAt!.Value > TimeSpan.FromHours(4))
            return await StopAll("MANUAL_REVIEW", "PRODUCT_UPDATE_BATCH_EXPIRED", JobExecutionResult.ManualReview("PRODUCT_UPDATE_BATCH_EXPIRED", "Ürün güncelleme batch sonucu dört saatlik pencerede alınamadı.", payload.ExternalOperationId));
        TrackRequest();
        var operationResult = await products.GetOperationAsync(Context(tenantId, connectionId, correlationId, $"{job.EffectIdempotencyKey}:{phase}:poll"), payload.ExternalOperationId!, cancellationToken);
        if (!operationResult.IsSuccess)
        {
            TrackResultFailure(operationResult.Error);
            var result = JobExecutionResult.FromAdapterError(operationResult.Error!);
            return result.Kind == JobCompletionKind.Retry ? result : await StopAll("MANUAL_REVIEW", operationResult.Error!.Code, result);
        }
        var status = operationResult.Value!;
        if (string.Equals(status.Status, "IN_PROGRESS", StringComparison.OrdinalIgnoreCase))
            return JobExecutionResult.Retry("PRODUCT_UPDATE_BATCH_PENDING", ProductUpdatePendingMessage(false, false), ProductUpdatePollDelay(payload.SubmittedAt.Value), payload.ExternalOperationId);
        if (!string.Equals(status.Status, "COMPLETED", StringComparison.OrdinalIgnoreCase))
            return await StopAll("MANUAL_REVIEW", "PRODUCT_UPDATE_BATCH_STATUS_UNKNOWN", JobExecutionResult.ManualReview("PRODUCT_UPDATE_BATCH_STATUS_UNKNOWN", "Ürün güncelleme batch servisi tanınmayan durum döndürdü.", payload.ExternalOperationId));
        var part = TrendyolUpdateGroups.Part(phase);
        if (part != "CONTENT" && status.Lines.Count == 0)
            return await StopAll("MANUAL_REVIEW", "PRODUCT_UPDATE_BATCH_CONTRACT_INVALID", JobExecutionResult.ManualReview("PRODUCT_UPDATE_BATCH_CONTRACT_INVALID", "Ürün güncelleme batch sonucu satır içermiyor.", payload.ExternalOperationId));

        // Each card's answer: index 0 is this card.
        var cards = new List<GroupCard> { new(job, payload) };
        cards.AddRange(others);
        var outcomes = new ProductUpdateGroupOutcome?[cards.Count];
        if (part == "CONTENT")
        {
            var (byCard, unattributed) = TrendyolUpdateGroups.Assign(cards.Select(card => (IReadOnlyCollection<string>)TrendyolUpdateGroups.Keys(card.Payload.ApprovedContentPayloadJson)).ToList(), status.Lines);
            var unknownFailure = unattributed.FirstOrDefault(line => !line.Succeeded);
            for (var index = 0; index < cards.Count; index++)
            {
                var failed = byCard[index].FirstOrDefault(line => !line.Succeeded) ?? unknownFailure;
                if (failed is null) continue;
                var code = SafeCode(failed.ErrorCode) ?? "PRODUCT_UPDATE_CONTENT_REJECTED";
                var message = byCard[index].Any(line => !line.Succeeded)
                    ? "Trendyol content güncellemesi reddedildi."
                    : "Ortak Trendyol content isteğinde hangi karta ait olduğu bilinmeyen bir içerik reddedildi; bu kartın sonucu kesinleşmedi.";
                outcomes[index] = new("BLOCKED", "UPDATE_REJECTED", code, "PRODUCT_UPDATE_CONTENT_REJECTED", message, payload.ExternalOperationId);
            }
        }
        else
        {
            var lines = status.Lines.Where(x => !string.IsNullOrWhiteSpace(x.ExternalKey)).GroupBy(x => x.ExternalKey, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < cards.Count; index++)
            {
                var (cardListings, cardStates) = index == 0
                    ? (listings, states)
                    : await LoadGroupCardListingsAsync(tenantId, connectionId, cards[index].Payload.ProfileId, cancellationToken);
                foreach (var listing in cardListings)
                {
                    if (string.IsNullOrWhiteSpace(listing.ExternalBarcode) || !lines.TryGetValue(listing.ExternalBarcode, out var line) || line.Succeeded) continue;
                    var code = SafeCode(line.ErrorCode) ?? "PRODUCT_UPDATE_REJECTED";
                    listing.ActualStatus = "UPDATE_REJECTED"; listing.RejectionCode = code;
                    if (cardStates.TryGetValue(listing.VariantId, out var state)) { state.ActualStatus = ListingSaleStatePolicy.AfterUpdate(state.ActualStatus, "UPDATE_REJECTED"); state.LastRejectionCode = code; state.Version++; }
                }
            }
        }

        // The other cards first. Their listings' answers are saved before their payloads move on: a run cut off in
        // between polls again (this card is still at its poll phase) and writes the same answers.
        var moves = new List<(GroupCard Card, ProductUpdateJobPayload Payload)>();
        for (var index = 1; index < cards.Count; index++)
        {
            var card = cards[index];
            if (outcomes[index] is { } outcome) { moves.Add((card, Stopped(card.Payload, outcome))); continue; }
            var next = AdvanceUpdate(card.Payload, phase);
            moves.Add((card, next ?? card.Payload with { Phase = TrendyolUpdateGroups.DonePhase, GroupReadySince = null, GroupLeaderJobId = null }));
            var cardProfile = await db.ChannelListingProfiles.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == card.Payload.ProfileId, cancellationToken);
            if (cardProfile is not null && next is not null) { cardProfile.ActualStatus = "UPDATE_IN_PROGRESS"; cardProfile.Version++; }
        }
        await db.SaveChangesAsync(cancellationToken);
        foreach (var (card, moved) in moves) await WriteGroupCardPayloadAsync(card, moved, cancellationToken);
        await WakeJobsAsync(others.Select(card => card.Job.Id).ToList(), cancellationToken);

        if (outcomes[0] is { } own)
            return await MarkUpdateResult(tenantId, connectionId, profile, own.ProfileStatus, own.RejectionCode, TrendyolUpdateGroups.Result(own), cancellationToken);
        var advanced = AdvanceUpdate(payload, phase);
        if (advanced is null) return await CompleteProductUpdate(tenantId, connectionId, payload, profile, listings, states, correlationId, cancellationToken);
        SetUpdatePayload(job, advanced);
        profile.ActualStatus = "UPDATE_IN_PROGRESS"; profile.Version++;
        await db.SaveChangesAsync(cancellationToken);
        return JobExecutionResult.Retry("PRODUCT_UPDATE_NEXT_PHASE", "Trendyol ürün güncellemesinin sonraki batch fazı hazırlanıyor.", TimeSpan.FromSeconds(1));
    }

    private static ProductUpdateJobPayload Stopped(ProductUpdateJobPayload card, ProductUpdateGroupOutcome outcome) =>
        card with { Phase = TrendyolUpdateGroups.StoppedPhase, GroupReadySince = null, GroupLeaderJobId = null, GroupOutcome = outcome };

    // Cards that end with the same answer as the one sending or polling for them; each one's next run records it.
    private async Task StopGroupCardsAsync(Guid tenantId, IReadOnlyList<GroupCard> cards, ProductUpdateGroupOutcome outcome, CancellationToken cancellationToken)
    {
        if (cards.Count == 0) return;
        foreach (var card in cards) await WriteGroupCardPayloadAsync(card, Stopped(card.Payload, outcome), cancellationToken);
        await WakeJobsAsync(cards.Select(card => card.Job.Id).ToList(), cancellationToken);
    }

    // Another card's payload, written in place: its job row's version and lease belong to its own runner.
    private async Task WriteGroupCardPayloadAsync(GroupCard card, ProductUpdateJobPayload payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload);
        var hash = Hash(json);
        await db.IntegrationJobs.Where(x => x.TenantId == card.Job.TenantId && x.Id == card.Job.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.PayloadJson, json).SetProperty(x => x.PayloadHash, hash), cancellationToken);
    }

    // A card the group answered runs again now instead of at its next waiting check.
    private async Task WakeJobsAsync(IReadOnlyCollection<Guid> jobIds, CancellationToken cancellationToken)
    {
        if (jobIds.Count == 0) return;
        var now = timeProvider.GetUtcNow();
        await db.IntegrationJobs.Where(x => jobIds.Contains(x.Id) && (x.Status == JobStatus.Pending || x.Status == JobStatus.RetryScheduled) && x.AvailableAt > now)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.AvailableAt, now), cancellationToken);
    }

    private void SetUpdatePayload(IntegrationJob job, ProductUpdateJobPayload payload)
    {
        job.PayloadJson = JsonSerializer.Serialize(payload);
        job.PayloadHash = Hash(job.PayloadJson);
    }

    private async Task<(IReadOnlyList<ChannelListingVariant> Listings, IReadOnlyDictionary<Guid, MarketplaceListingState> States)> LoadGroupCardListingsAsync(Guid tenantId, Guid connectionId, Guid profileId, CancellationToken cancellationToken)
    {
        var listings = await db.ChannelListingVariants.Where(x => x.TenantId == tenantId && x.ProfileId == profileId).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        var variantIds = listings.Select(x => x.VariantId).ToArray();
        var states = variantIds.Length == 0 ? new Dictionary<Guid, MarketplaceListingState>() : await db.MarketplaceListingStates.Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && variantIds.Contains(x.VariantId)).ToDictionaryAsync(x => x.VariantId, cancellationToken);
        return (listings, states);
    }

    // The card was updated again since this job was queued (its listings hold another payload hash).
    private async Task<bool> ProductUpdateSupersededAsync(Guid tenantId, Guid connectionId, ProductUpdateJobPayload payload, CancellationToken cancellationToken)
    {
        var variantIds = await db.ChannelListingVariants.AsNoTracking().Where(x => x.TenantId == tenantId && x.ProfileId == payload.ProfileId).Select(x => x.VariantId).ToListAsync(cancellationToken);
        if (variantIds.Count == 0) return true;
        var hashes = await db.MarketplaceListingStates.AsNoTracking().Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId && variantIds.Contains(x.VariantId)).Select(x => x.PayloadHash).ToListAsync(cancellationToken);
        return hashes.Count != variantIds.Count || hashes.Any(hash => !string.Equals(hash, payload.PayloadHash, StringComparison.Ordinal));
    }
}
